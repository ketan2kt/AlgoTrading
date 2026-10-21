using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingSystem.Application.Broker;
using TradingSystem.Application.Execution;
using TradingSystem.Application.MarketData;
using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.MarketData;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.Infrastructure.Execution;

internal sealed partial class MultiMarketPaperTradingService(
    IServiceScopeFactory scopeFactory,
    IGrowwReadOnlyGateway gateway,
    IOptions<AutomatedPaperTradingOptions> options,
    IOptions<MarketDataOptions> marketOptions,
    TimeProvider timeProvider,
    ILogger<MultiMarketPaperTradingService> logger) : BackgroundService
{
    private static readonly TimeZoneInfo IndiaTimeZone = FindIndiaTimeZone();
    private readonly Dictionary<string, DateTimeOffset> lastEvaluated = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, decimal> favourablePriceByPosition = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            var markets = options.Value.NaturalGasEnabled
                ? new[] { TradingMarketCatalog.Sensex, TradingMarketCatalog.NaturalGas }
                : [TradingMarketCatalog.Sensex];
            foreach (var market in markets)
            {
                try { await RunMarketAsync(market, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception exception) { LogCycleFailed(logger, market.Code, exception); }
            }
            await Task.Delay(TimeSpan.FromSeconds(options.Value.EvaluationIntervalSeconds), stoppingToken);
        }
    }

    private async Task RunMarketAsync(TradingMarketDefinition market, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var now = timeProvider.GetUtcNow();
        var india = TimeZoneInfo.ConvertTime(now, IndiaTimeZone);
        if (india.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return;
        var localTime = TimeOnly.FromDateTime(india.DateTime);
        if (localTime < market.SessionStart || localTime > market.SessionEnd) return;
        var today = DateOnly.FromDateTime(india.Date);
        var underlying = await FindUnderlyingAsync(db, market, today, cancellationToken);
        if (underlying is null) return;

        await ManagePositionsAsync(db, market, now, cancellationToken);
        await CaptureExitResearchAsync(db, market, now, cancellationToken);

        // Continue managing existing positions, but never open index trades at/after 15:00 IST.
        if (market == TradingMarketCatalog.Sensex &&
            !IndexEntryWindowPolicy.AllowsEntry(localTime,
                TimeOnly.ParseExact(options.Value.EntryWindowStart, "HH:mm"),
                TimeOnly.ParseExact(options.Value.EntryCutoff, "HH:mm"))) return;

        var interval = marketOptions.Value.CandleIntervalSeconds;
        var sessionStartUtc = new DateTimeOffset(india.Date, india.Offset).ToUniversalTime();
        var candles = await db.Candles.AsNoTracking().Where(value => value.InstrumentId == underlying.Id &&
                value.IntervalSeconds == interval && value.Source == "Groww" &&
                (market != TradingMarketCatalog.Sensex || value.OpenTimeUtc >= sessionStartUtc))
            .OrderByDescending(value => value.OpenTimeUtc).Take(90)
            .OrderBy(value => value.OpenTimeUtc).ToListAsync(cancellationToken);
        if (candles.Count < (market == TradingMarketCatalog.Sensex ? 12 : 21)) return;
        var latest = candles[^1];
        if (lastEvaluated.GetValueOrDefault(market.Code) == latest.OpenTimeUtc) return;
        lastEvaluated[market.Code] = latest.OpenTimeUtc;

        var priceBars = candles.Select(x =>
            new StrategyPriceBar(x.OpenTimeUtc, x.Open, x.High, x.Low, x.Close)).ToArray();
        var lifecycle = market == TradingMarketCatalog.Sensex
            ? SensexSetupLifecyclePolicy.Evaluate(priceBars)
            : null;
        if (lifecycle is not null)
        {
            db.MarketStrategyAudits.Add(new(Guid.NewGuid(), market.Code, underlying.Id,
                latest.OpenTimeUtc, "SensexSetupLifecycle", .50m,
                JsonSerializer.Serialize(lifecycle)));
            await db.SaveChangesAsync(cancellationToken);
        }
        var decision = market == TradingMarketCatalog.NaturalGas
            ? EvaluateNaturalGas(candles)
            : Evaluate(candles);
        MarketStateAssessmentV2? marketStateV2 = null;
        DecisionFeatureSnapshotV2? decisionSnapshotV2 = null;
        var sensexConfirmationReady = market != TradingMarketCatalog.Sensex;
        var sensexVwap = 0m;
        var sensexFast = 0m;
        var sensexSlow = 0m;
        var sensexRelativeVolume = 0m;
        PersistentDirectionalThesisV1? persistentThesis = null;
        if (market == TradingMarketCatalog.Sensex)
        {
            var closes = candles.Select(value => value.Close).ToArray();
            var fastV2 = TechnicalIndicators.ExponentialMovingAverage(closes, 9);
            var slowV2 = TechnicalIndicators.ExponentialMovingAverage(closes,
                Math.Min(21, closes.Length));
            var future = await db.Instruments.AsNoTracking().Where(value => value.Exchange == "BSE" &&
                    value.Type == InstrumentType.Future && value.TradingSymbol.StartsWith("SENSEX") &&
                    value.ExpiryDate >= today && value.IsActive)
                .OrderBy(value => value.ExpiryDate).ThenBy(value => value.TradingSymbol)
                .FirstOrDefaultAsync(cancellationToken);
            var futureCandles = future is null ? [] : await db.Candles.AsNoTracking()
                .Where(value => value.InstrumentId == future.Id && value.IntervalSeconds == interval &&
                                value.Source == "Groww" && value.OpenTimeUtc >= sessionStartUtc &&
                                value.OpenTimeUtc <= latest.OpenTimeUtc)
                .OrderByDescending(value => value.OpenTimeUtc).Take(40)
                .OrderBy(value => value.OpenTimeUtc).ToListAsync(cancellationToken);
            var volumeByTime = futureCandles.Where(value => value.Volume > 0)
                .ToDictionary(value => value.OpenTimeUtc, value => value.Volume);
            var aligned = candles.Where(value => volumeByTime.ContainsKey(value.OpenTimeUtc)).ToArray();
            var totalVolume = aligned.Sum(value => volumeByTime[value.OpenTimeUtc]);
            var latestFuture = futureCandles.LastOrDefault(value => value.Volume > 0);
            var baseline = futureCandles.Where(value => value.Volume > 0)
                .TakeLast(Math.Min(20, futureCandles.Count)).SkipLast(1).ToArray();
            var averageVolume = baseline.Length == 0 ? 0m : baseline.Average(value => (decimal)value.Volume);
            var relativeVolumeV2 = latestFuture is null || averageVolume <= 0m
                ? 0m : latestFuture.Volume / averageVolume;
            var vwapV2 = totalVolume <= 0m ? 0m : aligned.Sum(value =>
                ((value.High + value.Low + value.Close) / 3m) * volumeByTime[value.OpenTimeUtc]) /
                totalVolume;
            sensexVwap = vwapV2;
            sensexFast = fastV2;
            sensexSlow = slowV2;
            sensexRelativeVolume = relativeVolumeV2;
            sensexConfirmationReady = vwapV2 > 0m && relativeVolumeV2 > 0m;
            var stateV2 = MarketStateRouterV2.Analyze(priceBars, vwapV2, fastV2, slowV2);
            marketStateV2 = stateV2;
            var openingV2 = candles.Take(Math.Min(3, candles.Count)).ToArray();
            // Persist the multi-candle thesis before evaluating its strictly paper-only entry policy.
            persistentThesis = await DirectionalThesisShadowAudit.RecordAsync(db, market.Code, underlying.Id,
                latest.OpenTimeUtc, priceBars, vwapV2,
                openingV2.Max(value => value.High), openingV2.Min(value => value.Low),
                cancellationToken);
            var multiTimeframeV2 = MultiTimeframeDecisionPolicy.Analyze(priceBars,
                decision.Direction, openingV2.Max(value => value.High),
                openingV2.Min(value => value.Low));
            var researchAtrV2 = AverageTrueRange(candles.TakeLast(15).ToArray());
            decimal? researchStopV2 = decision.Direction == Direction.Buy
                ? latest.Close - researchAtrV2
                : decision.Direction == Direction.Sell ? latest.Close + researchAtrV2 : null;
            decimal? researchTargetV2 = decision.Direction == Direction.Buy
                ? latest.Close + researchAtrV2
                : decision.Direction == Direction.Sell ? latest.Close - researchAtrV2 : null;
            var snapshotV2 = new DecisionFeatureSnapshotV2("decision-dataset-v2", market.Code,
                latest.OpenTimeUtc, latest.Close, decision.Strategy, decision.Direction,
                decision.Confidence, stateV2, lifecycle?.Phase.ToString() ?? "Scanning",
                vwapV2, fastV2, slowV2,
                latest.Close <= 0m ? 0m : researchAtrV2 /
                    latest.Close * 100m,
                relativeVolumeV2, openingV2.Max(value => value.High),
                openingV2.Min(value => value.Low), researchStopV2, researchTargetV2,
                [.. decision.Reasons, .. multiTimeframeV2.Evidence], []);
            decisionSnapshotV2 = snapshotV2;
            db.MarketStrategyAudits.Add(new(Guid.NewGuid(), market.Code, underlying.Id,
                latest.OpenTimeUtc, "DecisionSnapshot:v2", decision.Confidence,
                JsonSerializer.Serialize(snapshotV2)));
            await db.SaveChangesAsync(cancellationToken);
        }
        // Only explicitly qualified Sensex challengers may become paper-only positions.
        var paperResearchEntry = false;
        var persistentDirectionalEntry = false;
        if (market == TradingMarketCatalog.Sensex && !sensexConfirmationReady)
        {
            await AddAuditAsync(db, market, underlying.Id, latest.OpenTimeUtc,
                "DataIncomplete", 0m,
                ["Fresh Sensex futures volume/VWAP confirmation is unavailable; entries fail closed."],
                cancellationToken);
            return;
        }
        if (market == TradingMarketCatalog.Sensex && persistentThesis is not null)
        {
            var persistentEntry = PersistentDirectionalPaperEntryPolicyV1.Evaluate(
                priceBars, persistentThesis);
            var hasActivePosition = await db.MarketPaperPositions.AsNoTracking().AnyAsync(value =>
                value.Market == market.Code && value.Status == "Active", cancellationToken);
            if (!hasActivePosition &&
                persistentEntry.Direction is not null &&
                (decision.Direction is null || decision.Direction == persistentEntry.Direction))
            {
                decision = new(persistentEntry.Direction.Value, persistentEntry.Confidence,
                    PersistentDirectionalPaperEntryPolicyV1.StrategyCode(market.Code),
                    persistentEntry.Reasons);
                paperResearchEntry = true;
                persistentDirectionalEntry = true;
            }
        }
        if (market == TradingMarketCatalog.Sensex && decision.Direction is null)
        {
            var early = SensexEarlyPullbackResearchPolicy.Evaluate(priceBars);
            if (early.Direction is null)
                early = SensexPaperResearchEntryPolicy.DeriveLifecycleCandidate(lifecycle!);
            db.MarketStrategyAudits.Add(new(Guid.NewGuid(), market.Code, underlying.Id,
                latest.OpenTimeUtc, "SensexEarlyPullbackCandidate", early.Confidence,
                JsonSerializer.Serialize(new { early.Direction, early.Reasons })));
            await db.SaveChangesAsync(cancellationToken);
            var hasActivePosition = await db.MarketPaperPositions.AsNoTracking().AnyAsync(value =>
                value.Market == market.Code && value.Status == "Active", cancellationToken);
            var researchDecision = SensexPaperResearchEntryPolicy.Evaluate(early.Direction,
                early.Confidence, lifecycle!, hasActivePosition, persistentThesis);
            if (!paperResearchEntry && researchDecision.Permitted)
            {
                decision = new(early.Direction!.Value, researchDecision.EffectiveConfidence,
                    "Sensex controlled early pullback research",
                    [.. early.Reasons, .. researchDecision.Reasons]);
                paperResearchEntry = true;
            }
            else if (!paperResearchEntry && early.Direction is not null)
            {
                await AddAuditAsync(db, market, underlying.Id, latest.OpenTimeUtc,
                    "PaperResearchCandidateRejected", early.Confidence,
                    researchDecision.Reasons, cancellationToken);
            }
            if (!paperResearchEntry && !hasActivePosition && marketStateV2 is not null)
            {
                var range = RangePlaybookV2.Evaluate(priceBars, marketStateV2);
                if (range.Direction is not null)
                {
                    decision = new(range.Direction.Value, range.Confidence,
                        "Sensex range-edge rejection", range.Reasons);
                    paperResearchEntry = true;
                }
            }
            if (!paperResearchEntry && !hasActivePosition && marketStateV2 is not null)
            {
                var directional = DirectionalParticipationPolicy.Evaluate(priceBars,
                    marketStateV2, sensexVwap, sensexFast, sensexSlow);
                if (directional.Direction is not null)
                {
                    decision = new(directional.Direction.Value, directional.Confidence,
                        DirectionalParticipationPolicy.SensexStrategy, directional.Reasons);
                    paperResearchEntry = true;
                }
            }
        }
        if (market == TradingMarketCatalog.Sensex && !paperResearchEntry &&
            marketStateV2 is not null)
        {
            var directional = DirectionalParticipationPolicy.Evaluate(priceBars,
                marketStateV2, sensexVwap, sensexFast, sensexSlow);
            if (directional.Direction is not null &&
                (decision.Direction is null || decision.Direction == directional.Direction))
            {
                var hasActivePosition = await db.MarketPaperPositions.AsNoTracking().AnyAsync(value =>
                    value.Market == market.Code && value.Status == "Active", cancellationToken);
                if (!hasActivePosition)
                {
                    decision = new(directional.Direction.Value, directional.Confidence,
                        DirectionalParticipationPolicy.SensexStrategy, directional.Reasons);
                    paperResearchEntry = true;
                }
            }
        }
        if (decision.Direction is null)
        {
            await AuditIfDueAsync(db, market, underlying.Id, latest.OpenTimeUtc, decision,
                cancellationToken);
            return;
        }
        if (market == TradingMarketCatalog.Sensex && !paperResearchEntry && lifecycle is not null &&
            (lifecycle.Direction != decision.Direction ||
             !SensexSetupLifecyclePolicy.AllowsChampionEntry(lifecycle)))
        {
            await AddAuditAsync(db, market, underlying.Id, latest.OpenTimeUtc,
                "EntryTimingRejected", decision.Confidence,
                [$"The Sensex setup phase {lifecycle.Phase} is not a valid first-entry window.",
                 .. lifecycle.Evidence], cancellationToken);
            return;
        }

        SensexAdaptiveSetupAssessment? adaptiveSetup = null;
        if (market == TradingMarketCatalog.Sensex)
        {
            var timingObservation = SensexTimingResearch.Observe(candles.Select(x =>
                    new StrategyPriceBar(x.OpenTimeUtc, x.Open, x.High, x.Low, x.Close)).ToArray(),
                decision.Direction.Value, AverageTrueRange(candles.TakeLast(15).ToArray()), now, interval);
            var adaptiveObservation = SensexAdaptiveSetupPolicy.Assess(candles.Select(x =>
                    new StrategyPriceBar(x.OpenTimeUtc, x.Open, x.High, x.Low, x.Close)).ToArray(),
                decision.Direction.Value, AverageTrueRange(candles.TakeLast(15).ToArray()),
                Math.Abs(candles[0].Open - candles[0].Close) >
                AverageTrueRange(candles.TakeLast(15).ToArray()));
            adaptiveSetup = adaptiveObservation;
            db.MarketStrategyAudits.Add(new(Guid.NewGuid(), market.Code, underlying.Id,
                latest.OpenTimeUtc, "SensexTimingCandidate", decision.Confidence,
                JsonSerializer.Serialize(new { timingObservation, adaptiveObservation })));
            await db.SaveChangesAsync(cancellationToken);
            if (!paperResearchEntry && !SensexAdaptiveSetupPolicy.AllowsMomentumEntry(adaptiveObservation))
            {
                if (SensexAdaptiveSetupPolicy.AllowsPaperResearchEntry(adaptiveObservation))
                {
                    await AddAuditAsync(db, market, underlying.Id, latest.OpenTimeUtc,
                        "PaperResearchCandidate", decision.Confidence,
                        ["Candidate retained for research only; no position or P&L was created.",
                         .. adaptiveObservation.Concerns], cancellationToken);
                    return;
                }
                else
                {
                await AddAuditAsync(db, market, underlying.Id, latest.OpenTimeUtc,
                    "AdaptiveSetupRejected", decision.Confidence,
                    [$"{adaptiveObservation.State} / {adaptiveObservation.Verdict}: " +
                     "momentum entry requires a preferred directional-trend setup.",
                     .. adaptiveObservation.Concerns,
                     .. adaptiveObservation.Limitations], cancellationToken);
                return;
                }
            }
            var previousCandidates = await db.Candles.AsNoTracking().Where(value =>
                    value.InstrumentId == underlying.Id && value.IntervalSeconds == interval &&
                    value.Source == "Groww" && value.OpenTimeUtc < sessionStartUtc)
                .OrderByDescending(value => value.OpenTimeUtc).Take(150)
                .OrderBy(value => value.OpenTimeUtc).ToListAsync(cancellationToken);
            var previousSession = previousCandidates
                .GroupBy(value => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value.OpenTimeUtc,
                    IndiaTimeZone).Date))
                .OrderByDescending(group => group.Key).FirstOrDefault()?.ToArray() ?? [];
            var openingBars = candles.Take(Math.Min(3, candles.Count)).ToArray();
            var location = MarketLocationPolicy.Evaluate(
                candles.Select(value => new StrategyPriceBar(value.OpenTimeUtc, value.Open,
                    value.High, value.Low, value.Close)).ToArray(),
                previousSession.Select(value => new StrategyPriceBar(value.OpenTimeUtc, value.Open,
                    value.High, value.Low, value.Close)).ToArray(), decision.Direction.Value,
                AverageTrueRange(candles.TakeLast(15).ToArray()),
                openingBars.Max(value => value.High), openingBars.Min(value => value.Low));
            if (!location.Permitted && !paperResearchEntry)
            {
                await AddAuditAsync(db, market, underlying.Id, latest.OpenTimeUtc,
                    "MarketLocationRejected", decision.Confidence,
                    [$"{location.Context}.", .. location.Reasons], cancellationToken);
                return;
            }

            var optionInterest = await OptionOpenInterestEntryContext.EvaluateAsync(db,
                underlying.Id, decision.Direction.Value, latest.Close,
                AverageTrueRange(candles.TakeLast(15).ToArray()), now, cancellationToken);
            var optionInterestOutcome = !optionInterest.Available
                ? "OptionOpenInterestUnavailable"
                : optionInterest.Permitted
                    ? "OptionOpenInterestAccepted"
                    : "OptionOpenInterestBarrierRejected";
            await AddAuditAsync(db, market, underlying.Id, latest.OpenTimeUtc,
                optionInterestOutcome, decision.Confidence, optionInterest.Reasons,
                cancellationToken);
            if (!optionInterest.Permitted) return;

            var priorSameDirectionExit = await (from priorPosition in db.MarketPaperPositions.AsNoTracking()
                join executionInstrument in db.Instruments.AsNoTracking()
                    on priorPosition.ExecutionInstrumentId equals executionInstrument.Id
                where priorPosition.Market == market.Code && priorPosition.Status != "Active" &&
                      priorPosition.ClosedAtUtc != null &&
                      (decision.Direction == Direction.Buy
                          ? executionInstrument.Type == InstrumentType.CallOption
                          : executionInstrument.Type == InstrumentType.PutOption)
                orderby priorPosition.ClosedAtUtc descending
                select priorPosition.ClosedAtUtc).FirstOrDefaultAsync(cancellationToken);
            var persistentDirectionalContext = marketStateV2 is not null &&
                ProfessionalTradeFramework.Map(marketStateV2) == ProfessionalTradeRegime.Directional;
            var reentry = StructuralReentryPolicy.Evaluate(priceBars,
                decision.Direction.Value, priorSameDirectionExit, persistentDirectionalContext);
            // The persistent paper thesis already proves a completed failed-countertrend
            // pullback, so do not require the same reset twice.
            if (!reentry.Permitted && !persistentDirectionalEntry)
            {
                await AddAuditAsync(db, market, underlying.Id, latest.OpenTimeUtc,
                    "StructuralReentryRejected", decision.Confidence, reentry.Reasons, cancellationToken);
                return;
            }

            var lastOppositeExit = await (from priorPosition in db.MarketPaperPositions.AsNoTracking()
                join executionInstrument in db.Instruments.AsNoTracking()
                    on priorPosition.ExecutionInstrumentId equals executionInstrument.Id
                where priorPosition.Market == market.Code && priorPosition.Status != "Active" &&
                      priorPosition.ClosedAtUtc != null && priorPosition.ClosedAtUtc >= sessionStartUtc &&
                      (decision.Direction == Direction.Buy
                          ? executionInstrument.Type == InstrumentType.PutOption
                          : executionInstrument.Type == InstrumentType.CallOption)
                orderby priorPosition.ClosedAtUtc descending
                select priorPosition.ClosedAtUtc).FirstOrDefaultAsync(cancellationToken);
            var flipDecision = DirectionFlipCooldownPolicy.Evaluate(lastOppositeExit, now);
            if (!flipDecision.Permitted)
            {
                await AddAuditAsync(db, market, underlying.Id, latest.OpenTimeUtc,
                    "DirectionFlipCooldown", decision.Confidence, flipDecision.Reasons,
                    cancellationToken);
                return;
            }
        }
        else if (market == TradingMarketCatalog.NaturalGas)
        {
            var entriesToday = await db.MarketPaperPositions.AsNoTracking().CountAsync(value =>
                value.Market == market.Code && value.OpenedAtUtc >= sessionStartUtc,
                cancellationToken);
            if (entriesToday >= NaturalGasMiniPositionPolicy.MaximumEntriesPerSession)
            {
                await AddAuditAsync(db, market, underlying.Id, latest.OpenTimeUtc, "SessionEntryLimit",
                    decision.Confidence, ["Natural Gas Mini session entry limit is reached."], cancellationToken);
                return;
            }
            var reentryCutoff = now.AddMinutes(-NaturalGasMiniPositionPolicy.ReentryCooldownMinutes);
            if (await db.MarketPaperPositions.AsNoTracking().AnyAsync(value =>
                    value.Market == market.Code && value.OpenedAtUtc >= reentryCutoff,
                    cancellationToken))
            {
                await AddAuditAsync(db, market, underlying.Id, latest.OpenTimeUtc, "ReentryCooldown",
                    decision.Confidence, ["Natural Gas Mini 90-minute re-entry cooldown is active."], cancellationToken);
                return;
            }
        }

        var preferredInTheMoneySteps = market == TradingMarketCatalog.Sensex && marketStateV2 is not null &&
                                      ProfessionalTradeFramework.Map(marketStateV2) ==
                                      ProfessionalTradeRegime.Directional ? 1 : 0;
        var execution = await SelectExecutionInstrumentAsync(db, market, decision.Direction.Value,
            latest.Close, today, preferredInTheMoneySteps, cancellationToken);
        if (execution is null)
        {
            await AddAuditAsync(db, market, underlying.Id, latest.OpenTimeUtc, "InstrumentUnavailable",
                decision.Confidence, ["No eligible execution contract is synchronised."], cancellationToken);
            return;
        }
        if (market == TradingMarketCatalog.Sensex)
        {
            var daysToExpiry = execution.ExpiryDate is { } expiry
                ? Math.Max(0, expiry.DayNumber - today.DayNumber)
                : -1;
            if (daysToExpiry == 0)
            {
                await AddAuditAsync(db, market, underlying.Id, latest.OpenTimeUtc,
                    "RegularExpiryEntryRejected", decision.Confidence,
                    ["Regular Sensex option entries are disabled on expiry day; Hero Zero is the dedicated expiry strategy."],
                    cancellationToken);
                return;
            }
            var evidenceGate = SensexAdaptiveSetupPolicy.EvaluateEvidenceGate(
                adaptiveSetup!, localTime, daysToExpiry, decision.Confidence);
            if (!evidenceGate.Permitted && !paperResearchEntry)
            {
                await AddAuditAsync(db, market, underlying.Id, latest.OpenTimeUtc,
                    "EvidenceGateRejected", decision.Confidence,
                    [$"Sensex evidence gate rejected a {daysToExpiry}-DTE entry at {localTime:HH:mm} IST.",
                     .. evidenceGate.Reasons], cancellationToken);
                return;
            }
        }
        if (await db.MarketPaperPositions.AnyAsync(value => value.Market == market.Code &&
                value.ExecutionInstrumentId == execution.Id && value.Status == "Active", cancellationToken))
            return;

        var quote = await gateway.GetQuoteAsync(new(market.Exchange, market.ExecutionSegment,
            execution.TradingSymbol), cancellationToken);
        var entry = market.ExecuteOptions ? quote.OfferPrice ?? quote.LastPrice : quote.LastPrice;
        if (entry <= 0) return;
        var tick = execution.TickSize > 0
            ? InstrumentPriceIncrement.FromGrowwInstrumentMaster(execution.TickSize)
            : 0.05m;
        var executionDirection = market.ExecuteOptions ? Direction.Buy : decision.Direction.Value;
        decimal stop;
        decimal target;
        int quantity;
        if (market == TradingMarketCatalog.NaturalGas)
        {
            if (!NaturalGasMiniPositionPolicy.IsSupportedContract(execution.LotSize))
            {
                await AddAuditAsync(db, market, underlying.Id, latest.OpenTimeUtc, "ContractRejected",
                    decision.Confidence,
                    [$"Natural Gas Mini lot size is {execution.LotSize}; expected official lot size " +
                     $"{NaturalGasMiniPositionPolicy.ExpectedLotSize}. No call was issued."],
                    cancellationToken);
                return;
            }
            var recent = candles.TakeLast(12).ToArray();
            var atr = AverageTrueRange(recent);
            var structuralStop = executionDirection == Direction.Buy
                ? RoundToTick(recent.Min(value => value.Low) - atr * 0.25m, tick)
                : RoundToTick(recent.Max(value => value.High) + atr * 0.25m, tick);
            stop = RoundToTick(NaturalGasMiniPositionPolicy.WidenStop(
                executionDirection, entry, structuralStop, atr), tick);
            var structuralRisk = Math.Abs(entry - stop);
            target = RoundToTick(executionDirection == Direction.Buy
                ? entry + structuralRisk * NaturalGasMiniPositionPolicy.TargetRiskMultiple
                : entry - structuralRisk * NaturalGasMiniPositionPolicy.TargetRiskMultiple, tick);
            quantity = NaturalGasMiniPositionPolicy.FixedQuantity;
            if (!PaperPriceGeometryPolicy.IsValid(executionDirection, entry, stop, target))
            {
                await AddAuditAsync(db, market, underlying.Id, latest.OpenTimeUtc, "InvalidPriceGeometry",
                    decision.Confidence,
                    [$"Rejected {executionDirection} call because entry {entry}, stop {stop}, and target {target} are not directionally valid."],
                    cancellationToken);
                return;
            }
        }
        else
        {
            var riskDistance = entry * options.Value.OptionStopLossPercent / 100m;
            stop = RoundToTick(entry - riskDistance, tick);
            target = RoundToTick(entry + riskDistance, tick);
            var lotsByRisk = Math.Max(1, (int)Math.Floor(5000m /
                Math.Max(Math.Abs(entry - stop) * execution.LotSize, 0.01m)));
            quantity = execution.LotSize * Math.Min(lotsByRisk, options.Value.MaximumOptionLots);
        }
        ProfessionalTradePlan? professionalPlan = null;
        if (market == TradingMarketCatalog.Sensex && marketStateV2 is not null)
        {
            var spreadPercent = quote.BidPrice is > 0m && quote.OfferPrice is > 0m
                ? (quote.OfferPrice.Value - quote.BidPrice.Value) / quote.OfferPrice.Value * 100m
                : (decimal?)null;
            var chargesPerLot = PaperTradingCostModel.CalculateOptionCharges([
                new(PaperOptionTransactionSide.Buy, entry, execution.LotSize),
                new(PaperOptionTransactionSide.Sell, target, execution.LotSize)]).Total;
            var grossRewardPerLot = Math.Max(0m, (target - entry) * execution.LotSize);
            var riskPerLot = Math.Max(.01m, (entry - stop) * execution.LotSize);
            var daysToExpiry = execution.ExpiryDate is { } planExpiry
                ? Math.Max(0, planExpiry.DayNumber - today.DayNumber) : 10;
            var correlatedNiftyExposure = await HasActiveNiftyExposureAsync(db,
                decision.Direction.Value, sessionStartUtc, cancellationToken);
            professionalPlan = ProfessionalTradeFramework.Assess(new(
                market.Code, persistentDirectionalEntry ? ProfessionalTradeRegime.Directional :
                    ProfessionalTradeFramework.Map(marketStateV2), decision.Confidence,
                sensexRelativeVolume, adaptiveSetup?.RoomToOpposingLevelAtr, spreadPercent,
                quote.Volume is { } optionVolume ? (long?)decimal.ToInt64(optionVolume) : null,
                quote.OpenInterest is { } optionOi ? (long?)decimal.ToInt64(optionOi) : null,
                daysToExpiry, null, null,
                Math.Max(0m, (grossRewardPerLot - chargesPerLot) / riskPerLot),
                correlatedNiftyExposure, 0,
                paperResearchEntry || adaptiveSetup?.Setup == "ContinuationOrRetest"));
            if (!professionalPlan.Permitted)
            {
                await AddAuditAsync(db, market, underlying.Id, latest.OpenTimeUtc,
                    "ProfessionalPlanRejected", decision.Confidence,
                    professionalPlan.Reasons, cancellationToken);
                return;
            }
            quantity = ProfessionalTradeFramework.ScaleQuantity(quantity, execution.LotSize,
                professionalPlan.QuantityFraction);
        }
        SensexReasoningSnapshot? reasoning = null;
        if (market == TradingMarketCatalog.Sensex)
        {
            var recentCutoff = now.AddMinutes(-60);
            var sameSide = await (from p in db.MarketPaperPositions.AsNoTracking()
                join i in db.Instruments.AsNoTracking() on p.ExecutionInstrumentId equals i.Id
                where p.Market == market.Code && i.Type == execution.Type &&
                    (p.Status == "Active" || p.ClosedAtUtc >= recentCutoff)
                select new { p.Status, p.RealisedPnl }).ToListAsync(cancellationToken);
            var charges = PaperTradingCostModel.CalculateOptionCharges([
                new(PaperOptionTransactionSide.Buy, entry, quantity),
                new(PaperOptionTransactionSide.Sell, target, quantity)]).Total;
            decimal? quoteAge = quote.LastTradeTimeEpochMilliseconds is { } epoch &&
                epoch is >= -62135596800000 and <= 253402300799999
                ? (decimal)(timeProvider.GetUtcNow() - DateTimeOffset.FromUnixTimeMilliseconds(epoch)).TotalSeconds : null;
            reasoning = SensexTradeReasoning.Assess(candles.Select(x => new StrategyPriceBar(
                    x.OpenTimeUtc, x.Open, x.High, x.Low, x.Close)).ToArray(), decision.Direction.Value,
                AverageTrueRange(candles.TakeLast(15).ToArray()), entry, stop, target, quantity,
                quote.BidPrice, quote.OfferPrice, quoteAge, charges,
                sameSide.Count(x => x.Status == "Active"),
                sameSide.Count(x => x.Status != "Active" && x.RealisedPnl < 0));
        }
        var recordedStrategy = persistentDirectionalEntry
            ? decision.Strategy
            : paperResearchEntry ? $"Research|{decision.Strategy}" : decision.Strategy;
        var position = new MarketPaperPosition(Guid.NewGuid(), market.Code, underlying.Id,
            execution.Id, market == TradingMarketCatalog.NaturalGas
                ? $"Manual execution alert · {decision.Strategy}" : recordedStrategy,
            executionDirection, quantity, entry, stop, target, now);
        db.MarketPaperPositions.Add(position);
        db.MarketStrategyAudits.Add(new(Guid.NewGuid(), market.Code, underlying.Id, latest.OpenTimeUtc,
            paperResearchEntry ? "PaperResearchPositionOpened" : "PaperPositionOpened",
            decision.Confidence, JsonSerializer.Serialize(new
            {
                positionId = position.Id,
                strategy = decision.Strategy,
                reasoning, professionalPlan,
                adaptiveSetup,
                paperResearchEntry,
                liveEligible = !paperResearchEntry,
                underlyingDirection = decision.Direction.ToString(),
                quoteSnapshot = market == TradingMarketCatalog.Sensex ? quote : null,
                direction = executionDirection.ToString(),
                confidence = decision.Confidence,
                entry,
                initialStop = stop,
                target,
                quantity,
                execution = execution.TradingSymbol,
                timing = market == TradingMarketCatalog.Sensex
                    ? SensexTimingResearch.Observe(candles.Select(x => new StrategyPriceBar(
                        x.OpenTimeUtc, x.Open, x.High, x.Low, x.Close)).ToArray(), decision.Direction.Value,
                        AverageTrueRange(candles.TakeLast(15).ToArray()), timeProvider.GetUtcNow(), interval)
                    : null,
                evaluationStartedAtUtc = now,
                paperEntryRecordedAtUtc = timeProvider.GetUtcNow(),
                reasons = decision.Reasons
            })));
        if (market == TradingMarketCatalog.Sensex)
        {
            if (decisionSnapshotV2?.CandidateDirection is null)
            {
                var linkedAtr = AverageTrueRange(candles.TakeLast(15).ToArray());
                var linkedSnapshot = decisionSnapshotV2! with
                {
                    Strategy = recordedStrategy,
                    CandidateDirection = decision.Direction,
                    CandidateConfidence = decision.Confidence,
                    SetupPhase = paperResearchEntry ? "ResearchEntryWindow" : "Candidate",
                    ProposedStop = decision.Direction == Direction.Buy
                        ? latest.Close - linkedAtr : latest.Close + linkedAtr,
                    ProposedTarget = decision.Direction == Direction.Buy
                        ? latest.Close + linkedAtr : latest.Close - linkedAtr,
                    SupportingEvidence = [.. decision.Reasons,
                        $"Linked to opened Sensex paper position {position.Id:N}."]
                };
                db.MarketStrategyAudits.Add(new(Guid.NewGuid(), market.Code, underlying.Id,
                    latest.OpenTimeUtc, "DecisionSnapshot:v2", decision.Confidence,
                    JsonSerializer.Serialize(linkedSnapshot)));
            }
            db.MarketStrategyAudits.Add(new(Guid.NewGuid(), market.Code, underlying.Id,
                latest.OpenTimeUtc, "TradeResearchLink:v2", decision.Confidence,
                JsonSerializer.Serialize(new
                {
                    positionId = position.Id,
                    decisionCandleTimeUtc = latest.OpenTimeUtc,
                    executionInstrumentId = execution.Id,
                    execution = execution.TradingSymbol,
                    entry,
                    initialStop = stop,
                    target,
                    strategy = recordedStrategy,
                    paperResearchEntry
                })));
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task ManagePositionsAsync(TradingDbContext db, TradingMarketDefinition market,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var active = await db.MarketPaperPositions.Where(value => value.Market == market.Code &&
            value.Status == "Active" && !value.Strategy.StartsWith("Hero Zero|") &&
            !value.Strategy.StartsWith(IntradayExpansionPairService.StrategyPrefix))
            .ToListAsync(cancellationToken);
        foreach (var position in active)
        {
            var instrument = await db.Instruments.AsNoTracking().FirstOrDefaultAsync(value =>
                value.Id == position.ExecutionInstrumentId, cancellationToken);
            if (instrument is null)
            {
                LogMissingExecutionInstrument(logger, market.Code, position.Id, position.ExecutionInstrumentId);
                continue;
            }
            GrowwQuote quote;
            try
            {
                quote = await gateway.GetQuoteAsync(new(market.Exchange, market.ExecutionSegment,
                    instrument.TradingSymbol), cancellationToken);
            }
            catch (GrowwApiException exception)
            {
                LogPositionQuoteUnavailable(logger, market.Code, position.Id,
                    instrument.TradingSymbol, exception);
                continue;
            }
            var price = quote.LastPrice;
            if (price <= 0) continue;
            var targetRiskMultiple = market == TradingMarketCatalog.NaturalGas
                ? NaturalGasMiniPositionPolicy.TargetRiskMultiple : 1m;
            var initialRisk = Math.Abs(position.Target - position.EntryPrice) / targetRiskMultiple;
            var positionRegime = position.Strategy.Contains("range", StringComparison.OrdinalIgnoreCase)
                ? ProfessionalTradeRegime.Range
                : position.Strategy.Contains("directional", StringComparison.OrdinalIgnoreCase) ||
                  position.Strategy.Contains("continuation", StringComparison.OrdinalIgnoreCase)
                    ? ProfessionalTradeRegime.Directional
                    : ProfessionalTradeRegime.Transition;
            var exitPlan = ProfessionalExitPolicy.For(positionRegime);
            var favourable = position.Direction == Direction.Buy ? price - position.EntryPrice : position.EntryPrice - price;
            var favourableExtreme = price;
            var trailing = position.StopLoss;
            var previousStop = position.StopLoss;
            if (market == TradingMarketCatalog.NaturalGas)
            {
                trailing = NaturalGasMiniPositionPolicy.ApplyTrailingStop(position.Direction,
                    position.EntryPrice, trailing, price, initialRisk);
            }
            else
            {
                if (!favourablePriceByPosition.TryGetValue(position.Id, out var favourablePrice))
                {
                    var prefix = $"PositionSample:{position.Id:N}:";
                    var samples = await db.MarketStrategyAudits.AsNoTracking().Where(value =>
                            value.Market == market.Code && value.Outcome.StartsWith(prefix))
                        .Select(value => value.ReasonsJson).ToListAsync(cancellationToken);
                    var prices = samples.Select(value => SensexResearchAuditParser.Parse(value, "price"))
                        .Where(value => value.HasValue).Select(value => value!.Value.Value).ToArray();
                    favourablePrice = prices.Length == 0 ? position.EntryPrice
                        : position.Direction == Direction.Buy ? prices.Max() : prices.Min();
                }
                favourablePrice = position.Direction == Direction.Buy
                    ? Math.Max(favourablePrice, price) : Math.Min(favourablePrice, price);
                favourablePriceByPosition[position.Id] = favourablePrice;
                favourableExtreme = favourablePrice;
                var initialProtectiveStop = position.Direction == Direction.Buy
                    ? position.EntryPrice - initialRisk
                    : position.EntryPrice + initialRisk;
                trailing = PaperProtectiveStopPolicy.Calculate(position.Direction,
                    position.EntryPrice, initialProtectiveStop, favourablePrice,
                    exitPlan.BreakEvenTriggerR,
                    exitPlan.TrailDistanceR,
                    exitPlan.ProfitLockTriggerR,
                    exitPlan.ProfitLockR);
                if (PeakFavourableForProtection(position.Direction, position.EntryPrice,
                        favourablePrice) >= initialRisk * exitPlan.BreakEvenTriggerR)
                {
                    var protectionCosts = PaperTradingCostModel.CalculateOptionCharges([
                        new(PaperOptionTransactionSide.Buy, position.EntryPrice, position.RemainingQuantity),
                        new(PaperOptionTransactionSide.Sell, Math.Max(.05m, trailing),
                            position.RemainingQuantity)]).Total;
                    trailing = PaperProtectiveStopPolicy.ProtectNetProfit(position.Direction,
                        position.EntryPrice, trailing, position.RemainingQuantity, protectionCosts);
                }
                trailing = position.Direction == Direction.Buy
                    ? Math.Max(position.StopLoss, trailing)
                    : Math.Min(position.StopLoss, trailing);
            }
            position.Mark(price, trailing);
            var sampleMinute = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute,
                0, TimeSpan.Zero);
            var sampleOutcome = $"PositionSample:{position.Id:N}:{sampleMinute:yyyyMMddHHmm}";
            if (!await db.MarketStrategyAudits.AsNoTracking().AnyAsync(value =>
                    value.Market == market.Code && value.Outcome == sampleOutcome, cancellationToken))
            {
                var livePnl = (position.Direction == Direction.Buy
                    ? price - position.EntryPrice : position.EntryPrice - price) *
                    position.RemainingQuantity + position.RealisedPnl;
                var peakFavourable = position.Direction == Direction.Buy
                    ? favourableExtreme - position.EntryPrice
                    : position.EntryPrice - favourableExtreme;
                var favourableR = initialRisk <= 0m ? 0m : Math.Max(0m, peakFavourable / initialRisk);
                var protectionStage = position.StopLoss == previousStop ? "Unchanged"
                    : position.StopLoss == position.EntryPrice ? "BreakEven"
                    : favourableR >= options.Value.BreakEvenTriggerRiskMultiple +
                                      options.Value.TrailingStopRiskMultiple ? "Trailing"
                    : "ProfitLocked";
                db.MarketStrategyAudits.Add(new(Guid.NewGuid(), market.Code,
                    position.UnderlyingInstrumentId, sampleMinute, sampleOutcome, 0m,
                    JsonSerializer.Serialize(new { positionId = position.Id, price,
                        initialStop = previousStop, stop = position.StopLoss, position.Target,
                        livePnl, favourableR, protectionStage, observedAtUtc = now })));
            }
            if (position.StopLoss != previousStop)
                db.MarketStrategyAudits.Add(new(Guid.NewGuid(), market.Code,
                    position.UnderlyingInstrumentId, now,
                    $"TrailingStop:{position.Id:N}", 0m, JsonSerializer.Serialize(new
                    { positionId = position.Id, previousStop, newStop = position.StopLoss, price,
                        observedAtUtc = now })));
            var stopHit = position.Direction == Direction.Buy ? price <= position.StopLoss : price >= position.StopLoss;
            var targetHit = position.Direction == Direction.Buy ? price >= position.Target : price <= position.Target;
            var expectedUnderlyingDirection = market.ExecuteOptions
                ? instrument.Type == InstrumentType.PutOption ? Direction.Sell : Direction.Buy
                : position.Direction;
            if (stopHit && market == TradingMarketCatalog.Sensex &&
                position.Strategy.StartsWith(PersistentDirectionalPaperEntryPolicyV1.StrategyPrefix,
                    StringComparison.Ordinal))
            {
                var multiplier = position.Direction == Direction.Buy ? 1m : -1m;
                var stopProtectsProfit = (position.StopLoss - position.EntryPrice) * multiplier > 0m;
                var adverseRiskMultiple = initialRisk <= 0m ? decimal.MaxValue :
                    Math.Max(0m, (position.EntryPrice - price) * multiplier / initialRisk);
                var underlyingInvalidated = !stopProtectsProfit &&
                    await HasConfirmedDirectionalInvalidationAsync(db,
                        position.UnderlyingInstrumentId, expectedUnderlyingDirection,
                        position.OpenedAtUtc, cancellationToken);
                var stopDecision = PersistentDirectionalStopPolicy.Evaluate(true,
                    !stopProtectsProfit, now - position.OpenedAtUtc,
                    adverseRiskMultiple, underlyingInvalidated);
                if (stopDecision.Deferred)
                {
                    stopHit = false;
                    var deferredOutcome =
                        $"DirectionalStopDeferred:{position.Id:N}:{sampleMinute:yyyyMMddHHmm}";
                    if (!await db.MarketStrategyAudits.AsNoTracking().AnyAsync(value =>
                            value.Market == market.Code && value.Outcome == deferredOutcome,
                            cancellationToken))
                    {
                        db.MarketStrategyAudits.Add(new(Guid.NewGuid(), market.Code,
                            position.UnderlyingInstrumentId, sampleMinute, deferredOutcome, 0m,
                            JsonSerializer.Serialize(new
                            {
                                positionId = position.Id,
                                price,
                                position.EntryPrice,
                                position.StopLoss,
                                adverseRiskMultiple,
                                stopDecision.Reasons,
                                paperOnly = true,
                                observedAtUtc = now
                            })));
                    }
                }
            }
            var continuationConfirmed = targetHit && options.Value.TargetExtensionEnabled &&
                                        exitPlan.ExtendTarget &&
                                        await HasContinuationStructureAsync(db, position.UnderlyingInstrumentId,
                                            expectedUnderlyingDirection, cancellationToken);
            if (market == TradingMarketCatalog.Sensex && continuationConfirmed &&
                positionRegime == ProfessionalTradeRegime.Directional &&
                !position.Strategy.StartsWith("PaperOnly|Pyramid|", StringComparison.Ordinal))
            {
                var addOnPrefix = $"PaperOnly|Pyramid|{position.Id:N}";
                var addOnExists = await db.MarketPaperPositions.AsNoTracking().AnyAsync(value =>
                    value.Market == market.Code && value.Strategy == addOnPrefix,
                    cancellationToken);
                var addOnStop = RoundToTick(Math.Max(position.EntryPrice + initialRisk * .20m,
                    price - initialRisk * .50m), instrument.TickSize > 0
                    ? InstrumentPriceIncrement.FromGrowwInstrumentMaster(instrument.TickSize) : .05m);
                var addOnRisk = Math.Max(0m, price - addOnStop) * instrument.LotSize;
                var addOnDecision = WinnerAddOnPolicy.Evaluate(
                    initialRisk <= 0m ? 0m : Math.Max(0m,
                        (favourableExtreme - position.EntryPrice) / initialRisk),
                    true, addOnExists, initialRisk * position.Quantity, addOnRisk);
                if (addOnDecision.Permitted)
                {
                    var addOnTarget = RoundToTick(price + initialRisk * 1.50m,
                        instrument.TickSize > 0
                            ? InstrumentPriceIncrement.FromGrowwInstrumentMaster(instrument.TickSize) : .05m);
                    var addOn = new MarketPaperPosition(Guid.NewGuid(), market.Code,
                        position.UnderlyingInstrumentId, position.ExecutionInstrumentId,
                        addOnPrefix, Direction.Buy, instrument.LotSize, price, addOnStop,
                        addOnTarget, now);
                    db.MarketPaperPositions.Add(addOn);
                    db.MarketStrategyAudits.Add(new(Guid.NewGuid(), market.Code,
                        position.UnderlyingInstrumentId, now, $"WinnerAddOn:{position.Id:N}", 0m,
                        JsonSerializer.Serialize(new { parentPositionId = position.Id,
                            addOnPositionId = addOn.Id, price, addOnStop, addOnTarget,
                            quantity = instrument.LotSize, addOnDecision.Reasons,
                            liveEligible = false })));
                }
            }
            var india = TimeZoneInfo.ConvertTime(now, IndiaTimeZone);
            var forced = market != TradingMarketCatalog.NaturalGas &&
                         TimeOnly.FromDateTime(india.DateTime) >= market.SessionEnd.AddMinutes(-15);
            var partialThreshold = position.Direction == Direction.Buy
                ? position.EntryPrice + initialRisk * exitPlan.PartialProfitR
                : position.EntryPrice - initialRisk * exitPlan.PartialProfitR;
            var partialReached = position.Direction == Direction.Buy
                ? price >= partialThreshold : price <= partialThreshold;
            if (market == TradingMarketCatalog.Sensex && options.Value.PartialProfitEnabled &&
                position.PartialExitQuantity == 0 && partialReached && position.RemainingQuantity > instrument.LotSize)
            {
                var totalLots = Math.Max(1, position.RemainingQuantity / instrument.LotSize);
                var partialLots = Math.Clamp((int)Math.Floor(totalLots * exitPlan.PartialExitFraction),
                    1, Math.Max(1, totalLots - 1));
                var partialQuantity = partialLots * instrument.LotSize;
                var partialCosts = PaperTradingCostModel.CalculateOptionCharges([
                    new(PaperOptionTransactionSide.Buy, position.EntryPrice, partialQuantity),
                    new(PaperOptionTransactionSide.Sell, price, partialQuantity)]).Total;
                position.TakePartial(price, partialQuantity, partialCosts);
                db.MarketStrategyAudits.Add(new(Guid.NewGuid(), market.Code,
                    position.UnderlyingInstrumentId, now, $"PartialProfit:{position.Id:N}", 0m,
                    JsonSerializer.Serialize(new { positionId = position.Id, price, partialQuantity,
                        remainingQuantity = position.RemainingQuantity, partialCosts,
                        triggerR = exitPlan.PartialProfitR, regime = positionRegime.ToString() })));
            }
            if (stopHit || (targetHit && !continuationConfirmed) || forced)
            {
                var costs = market.ExecuteOptions
                    ? PaperTradingCostModel.CalculateOptionCharges([
                        new(PaperOptionTransactionSide.Buy, position.EntryPrice, position.RemainingQuantity),
                        new(PaperOptionTransactionSide.Sell, price, position.RemainingQuantity)]).Total
                    : decimal.Round((position.EntryPrice + price) * position.RemainingQuantity * 0.0005m, 2);
                position.Close(price, costs, targetHit ? "TargetHit" : stopHit ? "StopLossHit" : "TimeExit", now);
                favourablePriceByPosition.Remove(position.Id);
                var grossPnl = position.RealisedPnl + position.CostsPaid;
                db.MarketStrategyAudits.Add(new(Guid.NewGuid(), market.Code,
                    position.UnderlyingInstrumentId, now, $"PositionClosed:{position.Id:N}", 0m,
                    JsonSerializer.Serialize(new { positionId = position.Id, exitPrice = price,
                        exitReason = position.Status, grossPnl, costs, netPnl = position.RealisedPnl,
                        closedAtUtc = now })));
            }
        }
        if (active.Count > 0) await db.SaveChangesAsync(cancellationToken);
    }

    private static decimal PeakFavourableForProtection(Direction direction, decimal entry,
        decimal favourablePrice) => direction == Direction.Buy
        ? favourablePrice - entry : entry - favourablePrice;

    private async Task CaptureExitResearchAsync(TradingDbContext db, TradingMarketDefinition market,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var closed = await db.MarketPaperPositions.AsNoTracking().Where(value =>
                value.Market == market.Code && value.ClosedAtUtc != null &&
                value.ClosedAtUtc >= now.AddMinutes(-70))
            .ToListAsync(cancellationToken);
        foreach (var position in closed)
        {
            foreach (var horizon in PaperResearchCadence.PostExitHorizonMinutes)
            {
                var scheduled = position.ClosedAtUtc!.Value.AddMinutes(horizon);
                if (now < scheduled || now > scheduled.AddMinutes(5)) continue;
                var outcome = $"PostExit{horizon}:{position.Id:N}";
                if (await db.MarketStrategyAudits.AsNoTracking().AnyAsync(value =>
                        value.Market == market.Code && value.Outcome == outcome, cancellationToken)) continue;
                var instrument = await db.Instruments.AsNoTracking().FirstOrDefaultAsync(value =>
                    value.Id == position.ExecutionInstrumentId, cancellationToken);
                if (instrument is null) continue;
                GrowwQuote quote;
                try
                {
                    quote = await gateway.GetQuoteAsync(new(market.Exchange, market.ExecutionSegment,
                        instrument.TradingSymbol), cancellationToken);
                }
                catch (GrowwApiException) { continue; }
                if (quote.LastPrice <= 0) continue;
                var hypotheticalPnl = (position.Direction == Direction.Buy
                    ? quote.LastPrice - position.EntryPrice : position.EntryPrice - quote.LastPrice) * position.Quantity;
                var incrementalAfterExit = (position.Direction == Direction.Buy
                    ? quote.LastPrice - position.CurrentPrice : position.CurrentPrice - quote.LastPrice) * position.Quantity;
                db.MarketStrategyAudits.Add(new(Guid.NewGuid(), market.Code,
                    position.UnderlyingInstrumentId, scheduled, outcome, 0m,
                    JsonSerializer.Serialize(new { positionId = position.Id, horizonMinutes = horizon,
                        observedPrice = quote.LastPrice, hypotheticalPnl, incrementalAfterExit,
                        scheduledAtUtc = scheduled, observedAtUtc = now })));
                await db.SaveChangesAsync(cancellationToken);
            }
        }
    }

    private async Task<bool> HasContinuationStructureAsync(TradingDbContext db, Guid underlyingInstrumentId,
        Direction direction, CancellationToken cancellationToken)
    {
        var candles = await db.Candles.AsNoTracking()
            .Where(value => value.InstrumentId == underlyingInstrumentId &&
                            value.IntervalSeconds == marketOptions.Value.CandleIntervalSeconds &&
                            value.Source == "Groww")
            .OrderByDescending(value => value.OpenTimeUtc)
            .Take(21)
            .OrderBy(value => value.OpenTimeUtc)
            .ToListAsync(cancellationToken);
        if (candles.Count < 9) return false;
        var closes = candles.Select(value => value.Close).ToArray();
        var fast = TechnicalIndicators.ExponentialMovingAverage(closes, 9);
        var slow = TechnicalIndicators.ExponentialMovingAverage(closes, Math.Min(21, closes.Length));
        var latest = candles[^1];
        return direction == Direction.Buy
            ? fast > slow && latest.Close >= fast
            : fast < slow && latest.Close <= fast;
    }

    private async Task<bool> HasConfirmedDirectionalInvalidationAsync(TradingDbContext db,
        Guid underlyingInstrumentId, Direction originalDirection, DateTimeOffset openedAtUtc,
        CancellationToken cancellationToken)
    {
        var candles = await db.Candles.AsNoTracking()
            .Where(value => value.InstrumentId == underlyingInstrumentId &&
                            value.IntervalSeconds == marketOptions.Value.CandleIntervalSeconds &&
                            value.Source == "Groww")
            .OrderByDescending(value => value.OpenTimeUtc)
            .Take(30)
            .OrderBy(value => value.OpenTimeUtc)
            .ToListAsync(cancellationToken);
        if (candles.Count < 8 || candles.Count(value =>
                value.OpenTimeUtc.AddSeconds(value.IntervalSeconds) > openedAtUtc) < 2)
            return false;
        var closes = candles.Select(value => value.Close).ToArray();
        var bars = candles.Select(value => new StrategyPriceBar(value.OpenTimeUtc,
            value.Open, value.High, value.Low, value.Close)).ToArray();
        return UnderlyingTrendInvalidationPolicy.Evaluate(originalDirection, bars,
            TechnicalIndicators.ExponentialMovingAverage(closes, 9),
            TechnicalIndicators.ExponentialMovingAverage(closes, Math.Min(21, closes.Length)),
            options.Value.MinimumReversalStructureStrength,
            options.Value.RequiredReversalEvidenceCount).ShouldExit;
    }

    private static MarketDecision Evaluate(IReadOnlyList<Candle> candles)
    {
        var result = IndexMomentumBreakoutPolicy.Evaluate(candles);
        return new(result.Direction, result.Confidence, "Index momentum breakout", result.Reasons);
    }

    private static MarketDecision EvaluateNaturalGas(IReadOnlyList<Candle> candles)
    {
        var closes = candles.Select(value => value.Close).ToArray();
        var fast = TechnicalIndicators.ExponentialMovingAverage(closes, 9);
        var slow = TechnicalIndicators.ExponentialMovingAverage(closes, 21);
        var latest = candles[^1];
        var prior = candles.TakeLast(9).SkipLast(1).ToArray();
        var atr = AverageTrueRange(candles.TakeLast(15).ToArray());
        var impulse = Math.Abs(latest.Close - latest.Open) >= atr * 0.60m;
        var bullish = fast > slow && latest.Close > prior.Max(value => value.High) &&
                      latest.Close > latest.Open && impulse;
        var bearish = fast < slow && latest.Close < prior.Min(value => value.Low) &&
                      latest.Close < latest.Open && impulse;
        var separation = Math.Abs(fast - slow) / latest.Close;
        var confidence = Math.Min(0.90m, 0.55m + separation * 100m);
        if (bullish) return new(Direction.Buy, confidence, "Natural Gas Mini positional breakout",
            ["EMA 9 is above EMA 21.", "Close confirmed above eight-candle structure.",
             "Breakout candle has at least 0.60 ATR directional range."]);
        if (bearish) return new(Direction.Sell, confidence, "Natural Gas Mini positional breakout",
            ["EMA 9 is below EMA 21.", "Close confirmed below eight-candle structure.",
             "Breakout candle has at least 0.60 ATR directional range."]);
        return new(null, confidence, "Natural Gas Mini positional breakout",
            ["No confirmed EMA-aligned eight-candle positional breakout."]);
    }

    private static async Task<Instrument?> FindUnderlyingAsync(TradingDbContext db, TradingMarketDefinition market,
        DateOnly today, CancellationToken cancellationToken)
    {
        var query = EfTradingWorkspaceReader.ScopeInstrumentQuery(db.Instruments.AsNoTracking(), market);
        return market.InstrumentType == InstrumentType.Index
            ? await query.FirstOrDefaultAsync(value => value.TradingSymbol == market.UnderlyingSymbol,
                cancellationToken)
            : await query.Where(value => value.TradingSymbol.StartsWith(market.UnderlyingSymbol) &&
                                         value.ExpiryDate >= today)
                .OrderBy(value => value.ExpiryDate).ThenBy(value => value.TradingSymbol)
                .FirstOrDefaultAsync(cancellationToken);
    }

    private static async Task<Instrument?> SelectExecutionInstrumentAsync(TradingDbContext db,
        TradingMarketDefinition market, Direction direction, decimal price, DateOnly today,
        int preferredInTheMoneySteps, CancellationToken cancellationToken)
    {
        if (!market.ExecuteOptions)
            return await db.Instruments.AsNoTracking().Where(value => value.Exchange == market.Exchange &&
                    value.Type == InstrumentType.Future && value.TradingSymbol.StartsWith(market.ExecutionUnderlying) &&
                    value.ExpiryDate >= today && value.IsActive)
                .OrderBy(value => value.ExpiryDate).FirstOrDefaultAsync(cancellationToken);
        var strikeStep = market == TradingMarketCatalog.Sensex ? 100m : 50m;
        var desiredStrike = direction == Direction.Buy
            ? price - preferredInTheMoneySteps * strikeStep
            : price + preferredInTheMoneySteps * strikeStep;
        var instruments = await db.Instruments.AsNoTracking().Where(value => value.Exchange == market.Exchange &&
                value.Segment == InstrumentSegment.FuturesAndOptions && value.IsActive && value.ExpiryDate >= today &&
                value.ExpiryDate <= today.AddDays(10) && value.StrikePrice != null &&
                (direction == Direction.Buy ? value.Type == InstrumentType.CallOption : value.Type == InstrumentType.PutOption) &&
                value.TradingSymbol.StartsWith(market.ExecutionUnderlying))
            .OrderBy(value => value.ExpiryDate).ThenBy(value => Math.Abs(value.StrikePrice!.Value - desiredStrike))
            .FirstOrDefaultAsync(cancellationToken);
        return instruments;
    }

    private static async Task<bool> HasActiveNiftyExposureAsync(TradingDbContext db,
        Direction direction, DateTimeOffset sessionStartUtc, CancellationToken cancellationToken)
    {
        var signalIds = await (from signal in db.Signals.AsNoTracking()
            join instrument in db.Instruments.AsNoTracking() on signal.InstrumentId equals instrument.Id
            where signal.MarketDataTimestampUtc >= sessionStartUtc && signal.Direction == direction &&
                  instrument.Exchange == "NSE" && instrument.TradingSymbol == "NIFTY"
            select signal.Id).ToListAsync(cancellationToken);
        if (signalIds.Count == 0) return false;
        var entryReferences = signalIds.Select(value => $"{value:N}-ENTRY").ToArray();
        var exitReferences = signalIds.Select(value => $"{value:N}-EXIT").ToArray();
        var filledEntries = await db.Orders.AsNoTracking().Where(value =>
                entryReferences.Contains(value.ClientReference) && value.State == OrderState.Filled)
            .Select(value => value.ClientReference).ToListAsync(cancellationToken);
        if (filledEntries.Count == 0) return false;
        var filledExits = await db.Orders.AsNoTracking().Where(value =>
                exitReferences.Contains(value.ClientReference) && value.State == OrderState.Filled)
            .Select(value => value.ClientReference).ToListAsync(cancellationToken);
        var exitedIds = filledExits.Select(value => value[..^"-EXIT".Length])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return filledEntries.Any(value => !exitedIds.Contains(value[..^"-ENTRY".Length]));
    }

    private async Task AuditIfDueAsync(TradingDbContext db, TradingMarketDefinition market, Guid instrumentId,
        DateTimeOffset candleTime, MarketDecision decision, CancellationToken cancellationToken)
    {
        var cutoff = candleTime.AddMinutes(-options.Value.NoTradeAuditIntervalMinutes);
        if (await db.MarketStrategyAudits.AsNoTracking().AnyAsync(value => value.Market == market.Code &&
            value.CandleTimeUtc >= cutoff, cancellationToken)) return;
        await AddAuditAsync(db, market, instrumentId, candleTime, "NoSignal", decision.Confidence,
            decision.Reasons, cancellationToken);
    }

    private static async Task AddAuditAsync(TradingDbContext db, TradingMarketDefinition market, Guid instrumentId,
        DateTimeOffset candleTime, string outcome, decimal confidence, IReadOnlyList<string> reasons,
        CancellationToken cancellationToken)
    {
        db.MarketStrategyAudits.Add(new(Guid.NewGuid(), market.Code, instrumentId, candleTime,
            outcome, confidence, JsonSerializer.Serialize(reasons)));
        await db.SaveChangesAsync(cancellationToken);
    }

    private static decimal RoundToTick(decimal value, decimal tick) =>
        Math.Max(tick, Math.Round(value / tick, MidpointRounding.AwayFromZero) * tick);
    private static decimal AverageTrueRange(Candle[] candles)
    {
        if (candles.Length < 2) throw new ArgumentException("At least two candles are required.", nameof(candles));
        var ranges = new List<decimal>(candles.Length - 1);
        for (var index = 1; index < candles.Length; index++)
            ranges.Add(Math.Max(candles[index].High - candles[index].Low,
                Math.Max(Math.Abs(candles[index].High - candles[index - 1].Close),
                    Math.Abs(candles[index].Low - candles[index - 1].Close))));
        return ranges.Average();
    }
    private static TimeZoneInfo FindIndiaTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("India Standard Time"); }
    }
    private sealed record MarketDecision(Direction? Direction, decimal Confidence, string Strategy,
        IReadOnlyList<string> Reasons);
    [LoggerMessage(Level = LogLevel.Error, Message = "{Market} paper automation cycle failed.")]
    private static partial void LogCycleFailed(ILogger logger, string market, Exception exception);
    [LoggerMessage(Level = LogLevel.Error,
        Message = "Paper position {PositionId} for {Market} references missing execution instrument {InstrumentId}; management was skipped.")]
    private static partial void LogMissingExecutionInstrument(
        ILogger logger, string market, Guid positionId, Guid instrumentId);
    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Skipping {Market} position {PositionId}: quote unavailable for {TradingSymbol}.")]
    private static partial void LogPositionQuoteUnavailable(ILogger logger, string market,
        Guid positionId, string tradingSymbol, Exception exception);
}
