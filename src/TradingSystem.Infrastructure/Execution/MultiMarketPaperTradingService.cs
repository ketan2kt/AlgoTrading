using System.Text.Json;
using System.Collections.Concurrent;
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
using TradingSystem.Infrastructure.Diagnostics;

namespace TradingSystem.Infrastructure.Execution;

internal sealed partial class MultiMarketPaperTradingService(
    IServiceScopeFactory scopeFactory,
    IGrowwReadOnlyGateway gateway,
    IOptions<AutomatedPaperTradingOptions> options,
    IOptions<MarketDataOptions> marketOptions,
    TimeProvider timeProvider,
    LiveExecutionWakeSignal liveWakeSignal,
    TradingEngineEvidenceRecorder evidenceRecorder,
    ILogger<MultiMarketPaperTradingService> logger) : BackgroundService
{
    private static readonly TimeZoneInfo IndiaTimeZone = FindIndiaTimeZone();
    private readonly Dictionary<string, DateTimeOffset> lastEvaluated = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, decimal> favourablePriceByPosition = new();
    private readonly ConcurrentDictionary<Guid, BankNiftyTrailState> bankTrailSamples = new();
    private readonly ConcurrentDictionary<string, bool> degradedManagement = new();
    private readonly Dictionary<string, string> entryPhases = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            foreach (var market in new[] { TradingMarketCatalog.Sensex, TradingMarketCatalog.BankNifty })
                await evidenceRecorder.RecordAsync(market.Code, "regular", "Startup", "Disabled",
                    "Automated trading is disabled by server configuration.", timeProvider.GetUtcNow(),
                    new { options.Value.Enabled }, stoppingToken);
            return;
        }
        await Task.WhenAll(RunEntryLoopAsync(stoppingToken), RunPositionLoopAsync(stoppingToken),
            RunPostExitResearchLoopAsync(stoppingToken));
    }

    private async Task RunPostExitResearchLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            foreach (var market in new[] { TradingMarketCatalog.Sensex, TradingMarketCatalog.BankNifty })
            {
                try
                {
                    var local = TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), IndiaTimeZone);
                    if (local.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ||
                        TimeOnly.FromDateTime(local.DateTime) < market.SessionStart ||
                        TimeOnly.FromDateTime(local.DateTime) > market.SessionEnd) continue;
                    await using var scope = scopeFactory.CreateAsyncScope();
                    await CaptureExitResearchAsync(scope.ServiceProvider.GetRequiredService<TradingDbContext>(),
                        market, timeProvider.GetUtcNow(), token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch (Exception exception)
                {
                    await evidenceRecorder.RecordAsync(market.Code, "post-exit", "Research", "Faulted",
                        "Post-exit observation failed; trading loops remain independent.", timeProvider.GetUtcNow(),
                        TradingEngineEvidenceRecorder.FailureEvidence(exception), token);
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(30), token);
        }
    }

    private async Task RunEntryLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var markets = options.Value.NaturalGasEnabled
                ? new[] { TradingMarketCatalog.Sensex, TradingMarketCatalog.BankNifty,
                    TradingMarketCatalog.NaturalGas }
                : [TradingMarketCatalog.Sensex, TradingMarketCatalog.BankNifty];
            foreach (var market in markets)
            {
                try
                {
                    if (degradedManagement.ContainsKey(market.Code))
                    {
                        await evidenceRecorder.RecordAsync(market.Code, "regular", "PositionManagement",
                            "ProtectionDegraded", "New entries paused until existing position management recovers.",
                            timeProvider.GetUtcNow(), new { market.Code }, stoppingToken);
                        continue;
                    }
                    await RunMarketAsync(market, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception exception)
                {
                    LogCycleFailed(logger, market.Code, exception);
                    await evidenceRecorder.RecordAsync(market.Code, "regular",
                        entryPhases.GetValueOrDefault(market.Code, "Startup"), "Faulted",
                        "Entry evaluation failed before completion.", timeProvider.GetUtcNow(),
                        TradingEngineEvidenceRecorder.FailureEvidence(exception), stoppingToken);
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(options.Value.EvaluationIntervalSeconds), stoppingToken);
        }
    }

    // Position protection must not wait for candle research or entry decisions to finish.
    private Task RunPositionLoopAsync(CancellationToken stoppingToken)
    {
        // Independent market loops; a slow Sensex request must not delay Bank Nifty protection.
        var markets = options.Value.NaturalGasEnabled
            ? new[] { TradingMarketCatalog.Sensex, TradingMarketCatalog.BankNifty,
                TradingMarketCatalog.Bankex, TradingMarketCatalog.NaturalGas }
            : [TradingMarketCatalog.Sensex, TradingMarketCatalog.BankNifty, TradingMarketCatalog.Bankex];
        return Task.WhenAll(markets.Select(market => RunMarketPositionLoopAsync(market, stoppingToken)));
    }

    private async Task RunMarketPositionLoopAsync(TradingMarketDefinition market, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            {
                try { await RunPositionCycleAsync(market, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception exception)
                {
                    degradedManagement[market.Code] = true;
                    LogCycleFailed(logger, market.Code, exception);
                    await evidenceRecorder.RecordAsync(market.Code, "positions", "PositionManagement",
                        "Faulted", "Position monitoring failed.", timeProvider.GetUtcNow(),
                        TradingEngineEvidenceRecorder.FailureEvidence(exception), stoppingToken);
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(options.Value.EvaluationIntervalSeconds), stoppingToken);
        }
    }

    private async Task RunPositionCycleAsync(TradingMarketDefinition market, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var india = TimeZoneInfo.ConvertTime(now, IndiaTimeZone);
        if (india.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return;
        var localTime = TimeOnly.FromDateTime(india.DateTime);
        if (localTime < market.SessionStart || localTime > market.SessionEnd) return;
        Guid[] ids;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            ids = await db.MarketPaperPositions.AsNoTracking().Where(value => value.Market == market.Code &&
                value.Status == "Active" && !value.Strategy.StartsWith("Hero Zero|") &&
                !value.Strategy.StartsWith(IntradayExpansionPairService.StrategyPrefix))
                .Select(value => value.Id).ToArrayAsync(cancellationToken);
        }
        var healthy = true;
        foreach (var id in ids)
            healthy &= await PositionManagementRecovery.RunAsync(async protectionOnly =>
            {
                // Never retry stale tracked entities: reload the position, obtain a fresh
                // quote and recompute against the latest committed stop/remaining units.
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(15));
                await ManagePositionsAsync(db, market, id, protectionOnly, deadline.Token);
            }, async (exception, stage) =>
            {
                LogCycleFailed(logger, market.Code, exception);
                degradedManagement[market.Code] = true;
                await evidenceRecorder.RecordAsync(market.Code, "positions", stage,
                    "Faulted", "Position management degraded; protective recovery attempted.",
                    timeProvider.GetUtcNow(), new { positionId = id,
                        failure = TradingEngineEvidenceRecorder.FailureEvidence(exception) }, cancellationToken);
            }, cancellationToken);
        if (healthy) degradedManagement.TryRemove(market.Code, out _);
    }

    private async Task RunMarketAsync(TradingMarketDefinition market, CancellationToken cancellationToken)
    {
        entryPhases[market.Code] = "ConfigurationAndInstrument";
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var lotConfiguration = await scope.ServiceProvider
            .GetRequiredService<ITradingLotConfigurationService>().GetAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var india = TimeZoneInfo.ConvertTime(now, IndiaTimeZone);
        if (india.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return;
        var localTime = TimeOnly.FromDateTime(india.DateTime);
        if (localTime < market.SessionStart || localTime > market.SessionEnd) return;
        var today = DateOnly.FromDateTime(india.Date);
        var underlying = await FindUnderlyingAsync(db, market, today, cancellationToken);
        if (underlying is null)
        {
            await evidenceRecorder.RecordAsync(market.Code, "regular", "Instrument", "InstrumentUnavailable",
                "The index instrument is not synchronised.", now,
                new { market.UnderlyingSymbol, market.Exchange }, cancellationToken);
            return;
        }

        // Position management runs independently; never open index trades at/after 15:00 IST.
        if (market == TradingMarketCatalog.Sensex &&
            !IndexEntryWindowPolicy.AllowsEntry(localTime,
                TimeOnly.ParseExact(options.Value.EntryWindowStart, "HH:mm"),
                TimeOnly.ParseExact(options.Value.EntryCutoff, "HH:mm")))
        {
            await evidenceRecorder.RecordAsync(market.Code, "regular", "EntryWindow", "OutsideEntryWindow",
                "New entries are outside the configured entry window.", now,
                new { localTime, options.Value.EntryWindowStart, options.Value.EntryCutoff }, cancellationToken);
            return;
        }

        entryPhases[market.Code] = "CompletedCandles";
        var interval = marketOptions.Value.CandleIntervalSeconds;
        var sessionStartUtc = new DateTimeOffset(india.Date, india.Offset).ToUniversalTime();
        var candles = await db.Candles.AsNoTracking().Where(value => value.InstrumentId == underlying.Id &&
                value.IntervalSeconds == interval && value.Source == "Groww" &&
                (market.InstrumentType != InstrumentType.Index || value.OpenTimeUtc >= sessionStartUtc))
            .OrderByDescending(value => value.OpenTimeUtc).Take(market.InstrumentType == InstrumentType.Index ? 600 : 90)
            .OrderBy(value => value.OpenTimeUtc).ToListAsync(cancellationToken);
        if (candles.Count < (market.InstrumentType == InstrumentType.Index ? 12 : 21))
        {
            await evidenceRecorder.RecordAsync(market.Code, "regular", "CompletedCandles", "WarmingUp",
                "Insufficient completed candles for evaluation.", now,
                new { count = candles.Count, minimum = market.InstrumentType == InstrumentType.Index ? 12 : 21,
                    interval }, cancellationToken);
            return;
        }
        var latest = candles[^1];
        if (lastEvaluated.GetValueOrDefault(market.Code) == latest.OpenTimeUtc)
        {
            await evidenceRecorder.RecordAsync(market.Code, "regular", "CompletedCandles", "WaitingForNextCandle",
                "This completed candle was already evaluated.", now,
                new { latest.OpenTimeUtc, interval, count = candles.Count }, cancellationToken);
            return;
        }
        await evidenceRecorder.RecordAsync(market.Code, "regular", "CompletedCandles", "Evaluating",
            "Evaluating a new completed candle.", now,
            new { latest.OpenTimeUtc, latest.Close, interval, count = candles.Count,
                lots = lotConfiguration.ForMarket(market.Code) }, cancellationToken);
        lastEvaluated[market.Code] = latest.OpenTimeUtc;
        entryPhases[market.Code] = "StrategyAndConfirmation";

        if (market is { Code: "bank-nifty" or "bankex" })
        {
            await RunBankIndexEntryAsync(db, market, underlying, candles, lotConfiguration,
                today, localTime, now, sessionStartUtc, cancellationToken);
            return;
        }

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
            var confirmation = SensexFutureConfirmationPolicy.Evaluate(priceBars,
                futureCandles.Select(value => new SensexFutureVolumeBar(
                    value.OpenTimeUtc, value.Volume)).ToArray(), interval);
            var vwapV2 = confirmation.Vwap;
            var relativeVolumeV2 = confirmation.RelativeVolume;
            sensexVwap = vwapV2;
            sensexFast = fastV2;
            sensexSlow = slowV2;
            sensexRelativeVolume = relativeVolumeV2;
            sensexConfirmationReady = confirmation.Ready;
            var stateV2 = MarketStateRouterV2.Analyze(priceBars, vwapV2, fastV2, slowV2);
            marketStateV2 = stateV2;
            var openingV2 = candles.Take(Math.Min(3, candles.Count)).ToArray();
            // The persisted thesis is one of the Sensex entry setups.
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
        var supplementalSensexEntry = false;
        var persistentDirectionalEntry = false;
        if (market == TradingMarketCatalog.Sensex && !sensexConfirmationReady)
        {
            await AddAuditAsync(db, market, underlying.Id, latest.OpenTimeUtc,
                "DataIncomplete", 0m,
                ["Fresh Sensex futures volume/VWAP confirmation is unavailable; entries fail closed."],
                cancellationToken);
            return;
        }
        if (market == TradingMarketCatalog.Sensex)
        {
            var hasActivePosition = await db.MarketPaperPositions.AsNoTracking()
                .AnyAsync(value => value.Market == market.Code &&
                    value.Status == "Active", cancellationToken);
            var selection = SensexEntrySelectionPolicy.Evaluate(
                new(decision.Direction, decision.Confidence, decision.Strategy,
                    decision.Reasons), priceBars, lifecycle!, persistentThesis,
                marketStateV2!, sensexVwap, sensexFast, sensexSlow,
                hasActivePosition);
            if (selection.EarlyCandidate is { } early)
            {
                db.MarketStrategyAudits.Add(new(Guid.NewGuid(), market.Code,
                    underlying.Id, latest.OpenTimeUtc,
                    "SensexEarlyPullbackCandidate", early.Confidence,
                    JsonSerializer.Serialize(new { early.Direction, early.Reasons })));
                await db.SaveChangesAsync(cancellationToken);
                if (selection.EarlyAssessment is { Permitted: false } rejected &&
                    early.Direction is not null)
                {
                    await AddAuditAsync(db, market, underlying.Id,
                        latest.OpenTimeUtc, "SensexEarlyPullbackRejected",
                        early.Confidence, rejected.Reasons, cancellationToken);
                }
            }
            decision = new(selection.Candidate.Direction,
                selection.Candidate.Confidence, selection.Candidate.Strategy,
                selection.Candidate.Reasons);
            supplementalSensexEntry = selection.Supplemental;
            persistentDirectionalEntry = selection.PersistentDirectional;
        }
        if (decision.Direction is null)
        {
            await AuditIfDueAsync(db, market, underlying.Id, latest.OpenTimeUtc, decision,
                cancellationToken);
            return;
        }
        if (market == TradingMarketCatalog.Sensex && !supplementalSensexEntry && lifecycle is not null &&
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
            if (!supplementalSensexEntry &&
                !SensexAdaptiveSetupPolicy.AllowsMomentumEntry(adaptiveObservation))
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
                .OrderByDescending(value => value.OpenTimeUtc).Take(2500)
                .OrderBy(value => value.OpenTimeUtc).ToListAsync(cancellationToken);
            var previousSession = previousCandidates
                .GroupBy(value => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value.OpenTimeUtc,
                    IndiaTimeZone).Date))
                .OrderByDescending(group => group.Key).Take(5)
                .OrderBy(group => group.Key).SelectMany(group => group.OrderBy(value => value.OpenTimeUtc))
                .ToArray();
            var completedSessionBars = candles
                .Where(value => value.OpenTimeUtc.AddSeconds(value.IntervalSeconds) <= now)
                .Select(value => new StrategyPriceBar(value.OpenTimeUtc,
                    value.Open, value.High, value.Low, value.Close)).ToArray();
            var openingBars = candles.Take(Math.Min(3, candles.Count)).ToArray();
            var structureAtr = AverageTrueRange(candles.TakeLast(15).ToArray());
            var structure = MarketStructureQualityAnalyzer.Analyze(completedSessionBars,
                decision.Direction.Value, latest.Close, sensexVwap,
                latest.Close <= 0m ? 0m : structureAtr / latest.Close * 100m,
                openingBars.Max(value => value.High), openingBars.Min(value => value.Low));
            var preQuote = NiftyPreQuoteEntryGatePolicy.Evaluate(localTime,
                completedSessionBars, previousSession.Select(value => new StrategyPriceBar(
                    value.OpenTimeUtc, value.Open, value.High, value.Low, value.Close)).ToArray(),
                decision.Direction.Value, structure, MultiTimeframeDecisionPolicy.Analyze(
                    completedSessionBars, decision.Direction, openingBars.Max(value => value.High),
                    openingBars.Min(value => value.Low)), interval, structureAtr,
                openingBars.Max(value => value.High), openingBars.Min(value => value.Low));
            if (!preQuote.Permitted)
            {
                await AddAuditAsync(db, market, underlying.Id, latest.OpenTimeUtc,
                    preQuote.RejectionOutcome, decision.Confidence,
                    preQuote.RejectionOutcome == "MarketLocationRejected"
                        ? [$"{preQuote.LocationContext}.", .. preQuote.Reasons] : preQuote.Reasons,
                    cancellationToken);
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

            var sameDirectionExits = from priorPosition in db.MarketPaperPositions.AsNoTracking()
                join executionInstrument in db.Instruments.AsNoTracking()
                    on priorPosition.ExecutionInstrumentId equals executionInstrument.Id
                where priorPosition.Market == market.Code && priorPosition.Status != "Active" &&
                      priorPosition.ClosedAtUtc != null && priorPosition.ClosedAtUtc >= sessionStartUtc &&
                      priorPosition.ClosedAtUtc <= now &&
                      (decision.Direction == Direction.Buy
                          ? executionInstrument.Type == InstrumentType.CallOption
                          : executionInstrument.Type == InstrumentType.PutOption)
                orderby priorPosition.ClosedAtUtc descending
                select new { priorPosition.ClosedAtUtc, priorPosition.Status, priorPosition.Strategy,
                    priorPosition.RealisedPnl, priorPosition.EntryPrice, priorPosition.Quantity };
            var priorSameDirectionPosition = await sameDirectionExits.FirstOrDefaultAsync(cancellationToken);
            var priorSameDirectionExit = priorSameDirectionPosition?.ClosedAtUtc;
            var priorIsSpecialty = priorSameDirectionPosition is { } priorSide &&
                (priorSide.Strategy.StartsWith("Hero Zero|", StringComparison.Ordinal) ||
                 priorSide.Strategy.StartsWith("Expansion Pair|", StringComparison.Ordinal) ||
                 priorSide.Strategy.StartsWith("PaperOnly|Pyramid|", StringComparison.Ordinal));
            if (priorSameDirectionPosition is { } prior && !priorIsSpecialty &&
                !SensexFullStopReentryPolicy.AllowsEntry(now, prior.ClosedAtUtc, prior.Status,
                    prior.RealisedPnl, prior.EntryPrice, prior.Quantity, options.Value.OptionStopLossPercent))
            {
                await AddAuditAsync(db, market, underlying.Id, latest.OpenTimeUtc,
                    "SensexFullStopReentryDeferred", decision.Confidence,
                    ["A full-sized same-direction stop occurred less than ten minutes ago; await a fresh setup."],
                    cancellationToken);
                return;
            }
            if (persistentDirectionalEntry && !SensexMorningThesisReentryPolicy.AllowsEntry(
                    localTime, true, priorSameDirectionExit))
            {
                await AddAuditAsync(db, market, underlying.Id, latest.OpenTimeUtc,
                    "MorningPersistentThesisReentryDeferred", decision.Confidence,
                    ["A same-direction position already exited this morning; thesis re-entry waits until 10:45 IST."],
                    cancellationToken);
                return;
            }
            var persistentDirectionalContext = persistentDirectionalEntry || (marketStateV2 is not null &&
                ProfessionalTradeFramework.Map(marketStateV2) == ProfessionalTradeRegime.Directional);
            var reentry = StructuralReentryPolicy.Evaluate(priceBars
                    .Where(bar => bar.OpenTimeUtc.AddSeconds(interval) <= now).ToArray(),
                decision.Direction.Value, priorSameDirectionExit, persistentDirectionalContext);
            // A persistent thesis is not fresh post-exit evidence. Continuation bases remain eligible.
            if (!reentry.Permitted)
            {
                await AddAuditAsync(db, market, underlying.Id, latest.OpenTimeUtc,
                    "StructuralReentryRejected", decision.Confidence, reentry.Reasons, cancellationToken);
                return;
            }

            // A specialty close cannot hide the last regular same-side trade,
            // including for the post-cooldown level retest.
            var recentRegular = priorIsSpecialty
                ? await sameDirectionExits.Where(value =>
                    !value.Strategy.StartsWith("Hero Zero|") && !value.Strategy.StartsWith("Expansion Pair|") &&
                    !value.Strategy.StartsWith("PaperOnly|Pyramid|")).FirstOrDefaultAsync(cancellationToken)
                : priorSameDirectionPosition;
            if (SensexFailedSetupReentryPolicy.Blocks(now, recentRegular?.ClosedAtUtc,
                    recentRegular?.Status, recentRegular?.RealisedPnl, marketStateV2, decision.Direction.Value))
            {
                await AddAuditAsync(db, market, underlying.Id, latest.OpenTimeUtc,
                    SensexFailedSetupReentryPolicy.Rejection, decision.Confidence,
                    [$"The last regular {(decision.Direction == Direction.Buy ? "CE" : "PE")} entry failed at a loss. " +
                     "Same-side re-entry across strikes waits for stronger directional follow-through or the 15-minute reset; " +
                     $"current regime {marketStateV2?.State}, efficiency {marketStateV2?.DirectionalEfficiency:P0}."],
                    cancellationToken);
                return;
            }

            var cashOpen = new DateTimeOffset(today.ToDateTime(market.SessionStart), india.Offset);
            var retry = SensexWeakRetryEntryPolicy.Evaluate(priceBars, decision.Direction.Value,
                cashOpen, now, interval, recentRegular?.ClosedAtUtc, recentRegular?.Status,
                recentRegular?.RealisedPnl, marketStateV2);
            if (!retry.Permitted)
            {
                await AddAuditAsync(db, market, underlying.Id, latest.OpenTimeUtc,
                    retry.Rejection!, decision.Confidence, retry.Reasons, cancellationToken);
                return;
            }
            var stalled = SensexStalledTrendEntryPolicy.Evaluate(persistentDirectionalEntry,
                priceBars, decision.Direction.Value, cashOpen, now, interval);
            if (!stalled.Permitted)
            {
                await AddAuditAsync(db, market, underlying.Id, latest.OpenTimeUtc,
                    stalled.Rejection!, decision.Confidence, stalled.Reasons, cancellationToken);
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
        var sensexExpiryRolled = market == TradingMarketCatalog.Sensex &&
            await db.Instruments.AsNoTracking().AnyAsync(value => value.IsActive &&
                value.Exchange == market.Exchange && value.TradingSymbol.StartsWith(market.ExecutionUnderlying) &&
                value.ExpiryDate == today && (decision.Direction == Direction.Buy
                    ? value.Type == InstrumentType.CallOption : value.Type == InstrumentType.PutOption), cancellationToken);
        var execution = await SelectExecutionInstrumentAsync(db, market, decision.Direction.Value,
            latest.Close, today, preferredInTheMoneySteps, cancellationToken);
        if (execution is null)
        {
            await AddAuditAsync(db, market, underlying.Id, latest.OpenTimeUtc, "InstrumentUnavailable",
                decision.Confidence,
                [sensexExpiryRolled
                    ? "No eligible next-expiry Sensex option contract is synchronised."
                    : "No eligible execution contract is synchronised."], cancellationToken);
            return;
        }
        if (market == TradingMarketCatalog.Sensex)
        {
            var daysToExpiry = execution.ExpiryDate is { } expiry
                ? Math.Max(0, expiry.DayNumber - today.DayNumber)
                : -1;
            var evidenceGate = SensexAdaptiveSetupPolicy.EvaluateEvidenceGate(
                adaptiveSetup!, localTime, daysToExpiry, decision.Confidence,
                rolledFromSameDayExpiry: sensexExpiryRolled);
            if (!evidenceGate.Permitted && !supplementalSensexEntry)
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
        {
            await AddAuditAsync(db, market, underlying.Id, latest.OpenTimeUtc, "DuplicateActiveContract",
                decision.Confidence, ["An active position already uses the selected option contract."], cancellationToken);
            return;
        }

        entryPhases[market.Code] = "OptionQuote";
        var quote = await gateway.GetQuoteAsync(new(market.Exchange, market.ExecutionSegment,
            execution.TradingSymbol), cancellationToken);
        var quoteReceivedAt = timeProvider.GetUtcNow();
        var entry = market.ExecuteOptions ? quote.OfferPrice ?? quote.LastPrice : quote.LastPrice;
        if (entry <= 0)
        {
            await AddAuditAsync(db, market, underlying.Id, latest.OpenTimeUtc, "OptionQuoteUnavailable",
                decision.Confidence, [$"No positive entry quote for {execution.TradingSymbol}."], cancellationToken);
            return;
        }
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
            (stop, target) = SensexOptionAdmissionPolicy.ProtectivePrices(
                entry, options.Value.OptionStopLossPercent, tick);
            var configuredLots = lotConfiguration.SensexLots;
            quantity = checked(execution.LotSize * configuredLots);
        }
        ProfessionalTradePlan? professionalPlan = null;
        if (market == TradingMarketCatalog.Sensex && marketStateV2 is not null)
        {
            var daysToExpiry = execution.ExpiryDate is { } planExpiry
                ? Math.Max(0, planExpiry.DayNumber - today.DayNumber) : 10;
            var correlatedNiftyExposure = await HasActiveNiftyExposureAsync(db,
                decision.Direction.Value, sessionStartUtc, cancellationToken);
            var professionalContext = new ProfessionalTradeAssessmentInput(
                market.Code, persistentDirectionalEntry ? ProfessionalTradeRegime.Directional :
                    ProfessionalTradeFramework.Map(marketStateV2), decision.Confidence,
                sensexRelativeVolume, adaptiveSetup?.RoomToOpposingLevelAtr, null, null, null,
                daysToExpiry, null, null,
                0m,
                correlatedNiftyExposure, 0,
                supplementalSensexEntry || adaptiveSetup?.Setup == "ContinuationOrRetest");
            professionalPlan = SensexOptionAdmissionPolicy.Evaluate(new(quote,
                options.Value.OptionStopLossPercent, tick, execution.LotSize, professionalContext)).Plan;
            if (professionalPlan is not { Permitted: true })
            {
                await AddAuditAsync(db, market, underlying.Id, latest.OpenTimeUtc,
                    "ProfessionalPlanRejected", decision.Confidence,
                    professionalPlan?.Reasons ?? ["Invalid Sensex option admission inputs."], cancellationToken);
                return;
            }
            // The saved lot count is a ceiling; the common cost-inclusive budget
            // determines whole lots after admission.
        }
        OptionRiskBudgetDecision? riskBudget = null;
        if (market.ExecuteOptions)
        {
            riskBudget = OptionRiskBudgetPolicy.Size(entry, stop, execution.LotSize,
                lotConfiguration.ForMarket(market.Code), market.Exchange);
            quantity = riskBudget.Quantity;
            if (quantity == 0)
            {
                await AddAuditAsync(db, market, underlying.Id, latest.OpenTimeUtc, "RiskBudgetRejected",
                    decision.Confidence, ["Selected lots or option stop geometry are invalid."], cancellationToken);
                return;
            }
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
                new(PaperOptionTransactionSide.Sell, target, quantity)], market.Exchange).Total;
            decimal? quoteAge = GrowwQuoteTimestamp.ToUtc(quote.LastTradeTimeEpochMilliseconds) is { } sourceAt
                ? (decimal)(timeProvider.GetUtcNow() - sourceAt).TotalSeconds : null;
            reasoning = SensexTradeReasoning.Assess(candles.Select(x => new StrategyPriceBar(
                    x.OpenTimeUtc, x.Open, x.High, x.Low, x.Close)).ToArray(), decision.Direction.Value,
                AverageTrueRange(candles.TakeLast(15).ToArray()), entry, stop, target, quantity,
                quote.BidPrice, quote.OfferPrice, quoteAge, charges,
                sameSide.Count(x => x.Status == "Active"),
                sameSide.Count(x => x.Status != "Active" && x.RealisedPnl < 0));
        }
        var recordedStrategy = decision.Strategy;
        var filledAt = timeProvider.GetUtcNow();
        if (filledAt - quoteReceivedAt > TimeSpan.FromSeconds(marketOptions.Value.MaximumAgeSeconds))
        {
            await AddAuditAsync(db, market, underlying.Id, latest.OpenTimeUtc, "EntryQuoteExpired",
                decision.Confidence, ["Entry processing outlived the quote freshness limit; re-evaluate on the next candle."], cancellationToken);
            return;
        }
        var position = new MarketPaperPosition(Guid.NewGuid(), market.Code, underlying.Id,
            execution.Id, market == TradingMarketCatalog.NaturalGas
                ? $"Manual execution alert · {decision.Strategy}" : recordedStrategy,
            executionDirection, quantity, entry, stop, target, filledAt);
        db.MarketPaperPositions.Add(position);
        db.MarketStrategyAudits.Add(new(Guid.NewGuid(), market.Code, underlying.Id, latest.OpenTimeUtc,
            "PaperPositionOpened",
            decision.Confidence, JsonSerializer.Serialize(new
            {
                positionId = position.Id,
                strategy = decision.Strategy,
                reasoning, professionalPlan, riskBudget,
                appliedExitPlan = ProfessionalExitPolicy.For(MarketOptionPositionPolicy.Regime(recordedStrategy)),
                professionalPlanIsAdmissionAdvisory = true,
                adaptiveSetup,
                supplementalSensexEntry,
                liveMirrorCandidate = market == TradingMarketCatalog.Sensex && stop > .05m,
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
                quoteReceivedAtUtc = quoteReceivedAt,
                paperEntryRecordedAtUtc = filledAt,
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
                    SetupPhase = supplementalSensexEntry ? "SensexSetupCandidate" : "Candidate",
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
                    supplementalSensexEntry
                })));
        }
        await db.SaveChangesAsync(cancellationToken);
        if (market == TradingMarketCatalog.Sensex)
            scope.ServiceProvider.GetRequiredService<LiveExecutionWakeSignal>().Notify();
    }

    private async Task RunBankIndexEntryAsync(TradingDbContext db, TradingMarketDefinition market,
        Instrument underlying, IReadOnlyList<Candle> candles,
        TradingLotConfiguration lotConfiguration, DateOnly today, TimeOnly localTime,
        DateTimeOffset now, DateTimeOffset sessionStartUtc, CancellationToken cancellationToken)
    {
        if (!IndexEntryWindowPolicy.AllowsEntry(localTime,
                TimeOnly.ParseExact(options.Value.EntryWindowStart, "HH:mm"),
                TimeOnly.ParseExact(options.Value.EntryCutoff, "HH:mm")))
        {
            await AddAuditAsync(db, market, underlying.Id, candles[^1].OpenTimeUtc,
                "OutsideEntryWindow", 0m, ["New entries are outside the configured entry window."], cancellationToken);
            return;
        }
        var latest = candles[^1];
        var interval = marketOptions.Value.CandleIntervalSeconds;
        if (latest.OpenTimeUtc.AddSeconds(interval) > now ||
            now - latest.OpenTimeUtc > TimeSpan.FromMinutes(3))
        {
            await AddAuditAsync(db, market, underlying.Id, latest.OpenTimeUtc,
                "CandleUnavailable", 0m,
                [$"Latest candle {latest.OpenTimeUtc:O} is incomplete or more than three minutes old at {now:O}."],
                cancellationToken);
            return;
        }
        var decision = Evaluate(candles);
        if (decision.Direction is null)
        {
            await AuditIfDueAsync(db, market, underlying.Id, latest.OpenTimeUtc,
                decision, cancellationToken);
            return;
        }
        if (await db.MarketPaperPositions.AsNoTracking().AnyAsync(value =>
                value.Market == market.Code && value.Status == "Active", cancellationToken))
        {
            await AddAuditAsync(db, market, underlying.Id, latest.OpenTimeUtc,
                "ActivePosition", decision.Confidence,
                ["An existing position is active; no additional banking-index entry was opened."], cancellationToken);
            return;
        }

        var completed = candles.Where(value => value.OpenTimeUtc.AddSeconds(interval) <= now)
            .Select(value => new StrategyPriceBar(value.OpenTimeUtc, value.Open,
                value.High, value.Low, value.Close)).ToArray();
        var atr = AverageTrueRange(candles.TakeLast(15).ToArray());
        var previousCandles = await db.Candles.AsNoTracking().Where(value =>
                value.InstrumentId == underlying.Id && value.IntervalSeconds == interval &&
                value.Source == "Groww" && value.OpenTimeUtc < sessionStartUtc)
            .OrderByDescending(value => value.OpenTimeUtc).Take(390)
            .OrderBy(value => value.OpenTimeUtc).ToListAsync(cancellationToken);
        var priorDate = previousCandles.Count == 0 ? (DateOnly?)null :
            DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(
                previousCandles[^1].OpenTimeUtc, IndiaTimeZone).Date);
        var previous = previousCandles.Where(value => priorDate is not null &&
                DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value.OpenTimeUtc,
                    IndiaTimeZone).Date) == priorDate)
            .Select(value => new StrategyPriceBar(value.OpenTimeUtc, value.Open,
                value.High, value.Low, value.Close)).ToArray();
        var opening = candles.Take(3).ToArray();
        var openingHigh = opening.Max(value => value.High);
        var openingLow = opening.Min(value => value.Low);
        var preQuote = BankIndexPreQuoteGatePolicy.Evaluate(completed, previous,
            decision.Direction.Value, atr, openingHigh, openingLow, interval);
        if (preQuote.RejectionOutcome == "ChopConfirmationRejected")
        {
            await AddAuditAsync(db, market, underlying.Id, latest.OpenTimeUtc,
                preQuote.RejectionOutcome, decision.Confidence, preQuote.Reasons, cancellationToken);
            return;
        }
        if (!preQuote.Location.Permitted)
        {
            await AddAuditAsync(db, market, underlying.Id, latest.OpenTimeUtc,
                preQuote.RejectionOutcome, decision.Confidence, preQuote.Reasons, cancellationToken);
            return;
        }
        if (!preQuote.Permitted)
        {
            await AddAuditAsync(db, market, underlying.Id, latest.OpenTimeUtc,
                preQuote.RejectionOutcome, decision.Confidence,
                preQuote.Reasons, cancellationToken);
            return;
        }
        var priorBankExit = await (from priorPosition in db.MarketPaperPositions.AsNoTracking()
            join priorInstrument in db.Instruments.AsNoTracking()
                on priorPosition.ExecutionInstrumentId equals priorInstrument.Id
            where priorPosition.Market == market.Code && priorPosition.ClosedAtUtc >= sessionStartUtc &&
                priorPosition.Status != "Active" &&
                (decision.Direction == Direction.Buy ? priorInstrument.Type == InstrumentType.CallOption :
                    priorInstrument.Type == InstrumentType.PutOption)
            orderby priorPosition.ClosedAtUtc descending
            select priorPosition.ClosedAtUtc).FirstOrDefaultAsync(cancellationToken);
        var reentry = StructuralReentryPolicy.Evaluate(completed, decision.Direction.Value, priorBankExit);
        if (!reentry.Permitted)
        {
            await AddAuditAsync(db, market, underlying.Id, latest.OpenTimeUtc,
                "StructuralReentryRejected", decision.Confidence, reentry.Reasons, cancellationToken);
            return;
        }
        if (market == TradingMarketCatalog.BankNifty)
        {
            var oppositeType = decision.Direction == Direction.Buy ? InstrumentType.PutOption : InstrumentType.CallOption;
            var since = now.AddMinutes(-BankNiftyFailedSetupCooldownPolicy.Minutes);
            var oppositeFailedExit = await (from prior in db.MarketPaperPositions.AsNoTracking()
                join instrument in db.Instruments.AsNoTracking() on prior.ExecutionInstrumentId equals instrument.Id
                where prior.Market == market.Code && prior.Strategy == BankNiftyFailedSetupCooldownPolicy.Strategy &&
                    prior.Status == BankNiftyFailedSetupCooldownPolicy.FailedExit && prior.RealisedPnl < 0m &&
                    prior.ClosedAtUtc > since && prior.ClosedAtUtc <= now && instrument.Type == oppositeType
                orderby prior.ClosedAtUtc descending
                select prior.ClosedAtUtc).FirstOrDefaultAsync(cancellationToken);
            var structure = BankNiftyEntryStructurePolicy.Evaluate(completed, decision.Direction.Value,
                atr, interval, now, oppositeFailedExit);
            if (!structure.Permitted)
            {
                await AddAuditAsync(db, market, underlying.Id, latest.OpenTimeUtc,
                    structure.Rejection!, decision.Confidence, structure.Reasons, cancellationToken);
                return;
            }
        }
        var optionInterest = await OptionOpenInterestEntryContext.EvaluateAsync(db,
            underlying.Id, decision.Direction.Value, latest.Close, atr, now, cancellationToken);
        if (!optionInterest.Permitted)
        {
            await AddAuditAsync(db, market, underlying.Id, latest.OpenTimeUtc,
                "OptionOpenInterestBarrierRejected", decision.Confidence,
                optionInterest.Reasons, cancellationToken);
            return;
        }
        // Regular banking-index entries use the next monthly contract on an expiry
        // day. Selecting today's contract and rejecting it would strand the market.
        var execution = await SelectExecutionInstrumentAsync(db, market,
            decision.Direction.Value, latest.Close, today, 0,
            cancellationToken);
        if (execution is null)
        {
            await AddAuditAsync(db, market, underlying.Id, latest.OpenTimeUtc,
                "InstrumentUnavailable", decision.Confidence,
                ["No current monthly option contract is synchronized."], cancellationToken);
            return;
        }
        var cooldownAt = timeProvider.GetUtcNow();
        var cutoff = cooldownAt.AddMinutes(-BankNiftyFailedSetupCooldownPolicy.Minutes);
        var failedExit = await db.MarketPaperPositions.AsNoTracking()
            .Where(value => value.Market == market.Code && value.ExecutionInstrumentId == execution.Id &&
                value.Strategy == BankNiftyFailedSetupCooldownPolicy.Strategy &&
                value.Status == BankNiftyFailedSetupCooldownPolicy.FailedExit && value.RealisedPnl < 0m &&
                value.ClosedAtUtc > cutoff && value.ClosedAtUtc <= cooldownAt)
            .OrderByDescending(value => value.ClosedAtUtc).Select(value => value.ClosedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        if (failedExit is { } closedAt && BankNiftyFailedSetupCooldownPolicy.Blocks(cooldownAt, closedAt))
        {
            await AddAuditAsync(db, market, underlying.Id, latest.OpenTimeUtc,
                BankNiftyFailedSetupCooldownPolicy.Rejection, decision.Confidence,
                [$"{execution.TradingSymbol} had a losing failed-setup exit at {closedAt:O}; " +
                 $"same-contract entries resume at {closedAt.AddMinutes(BankNiftyFailedSetupCooldownPolicy.Minutes):O}."],
                cancellationToken);
            return;
        }
        entryPhases[market.Code] = "BankOptionQuoteAndAdmission";
        var quote = await gateway.GetQuoteAsync(new(market.Exchange, market.ExecutionSegment,
            execution.TradingSymbol), cancellationToken);
        var tick = execution.TickSize > 0m
            ? InstrumentPriceIncrement.FromGrowwInstrumentMaster(execution.TickSize) : .05m;
        var admission = BankIndexOptionAdmissionPolicy.Evaluate(quote.LastPrice,
            quote.BidPrice, quote.OfferPrice, execution.LotSize, tick,
            options.Value.OptionStopLossPercent);
        if (admission.SpreadRejected)
        {
            await AddAuditAsync(db, market, underlying.Id, latest.OpenTimeUtc,
                "OptionSpreadRejected", decision.Confidence,
                ["The selected option bid/ask spread exceeds the shared 1.5% quality limit."],
                cancellationToken);
            return;
        }
        if (!admission.Permitted)
        {
            await AddAuditAsync(db, market, underlying.Id, latest.OpenTimeUtc,
                "OptionGeometryRejected", decision.Confidence,
                [$"Invalid option entry/lot/tick or protective geometry: entry={admission.Entry}, " +
                 $"stop={admission.Stop}, target={admission.Target}, lot={execution.LotSize}, tick={tick}."],
                cancellationToken);
            return;
        }
        var entry = admission.Entry;
        var stop = admission.Stop;
        var target = admission.Target;
        var riskBudget = OptionRiskBudgetPolicy.Size(entry, stop, execution.LotSize,
            lotConfiguration.ForMarket(market.Code), market.Exchange);
        var quantity = riskBudget.Quantity;
        if (quantity == 0)
        {
            await AddAuditAsync(db, market, underlying.Id, latest.OpenTimeUtc, "RiskBudgetRejected",
                decision.Confidence, ["Selected lots or option stop geometry are invalid."], cancellationToken);
            return;
        }
        var filledAt = timeProvider.GetUtcNow();
        var position = new MarketPaperPosition(Guid.NewGuid(), market.Code, underlying.Id,
            execution.Id, BankNiftyFailedSetupCooldownPolicy.Strategy, Direction.Buy,
            quantity, entry, stop, target, filledAt);
        db.MarketPaperPositions.Add(position);
        db.MarketStrategyAudits.Add(new(Guid.NewGuid(), market.Code, underlying.Id,
            latest.OpenTimeUtc, "PaperPositionOpened", decision.Confidence,
            JsonSerializer.Serialize(new { positionId = position.Id, strategy = position.Strategy,
                underlyingDirection = decision.Direction.ToString(),
                direction = Direction.Buy.ToString(), entry, initialStop = stop, target,
                quantity, riskBudget, execution = execution.TradingSymbol, quoteSnapshot = quote,
                appliedExitPlan = MarketOptionPositionPolicy.ExitPlan(market.Code, position.Strategy),
                evaluationStartedAtUtc = now, paperEntryRecordedAtUtc = filledAt,
                reasons = decision.Reasons, location = preQuote.Location.Context,
                optionInterest = optionInterest.Reasons })));
        await db.SaveChangesAsync(cancellationToken);
        liveWakeSignal.Notify();
    }

    private async Task ManagePositionsAsync(TradingDbContext db, TradingMarketDefinition market,
        Guid positionId, bool protectionOnly, CancellationToken cancellationToken)
    {
        var active = await db.MarketPaperPositions.Where(value => value.Id == positionId && value.Market == market.Code &&
            value.Status == "Active" && !value.Strategy.StartsWith("Hero Zero|") &&
            !value.Strategy.StartsWith(IntradayExpansionPairService.StrategyPrefix))
            .ToListAsync(cancellationToken);
        var wakeForTrailExit = false;
        foreach (var position in active)
        {
            var instrument = await db.Instruments.AsNoTracking().FirstOrDefaultAsync(value =>
                value.Id == position.ExecutionInstrumentId, cancellationToken);
            if (instrument is null)
            {
                LogMissingExecutionInstrument(logger, market.Code, position.Id, position.ExecutionInstrumentId);
                throw new InvalidOperationException("Active position execution instrument is unavailable.");
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
                throw;
            }
            var price = quote.LastPrice;
            if (price <= 0) throw new InvalidOperationException("Active position quote is non-positive.");
            // Timestamp the observed quote after retrieval, not before a potentially slow cycle.
            var observedAt = timeProvider.GetUtcNow();
            var manualExitRequested = await ManualTradeExitRequest.ExistsAsync(db, market.Code,
                position.Id, cancellationToken);
            var targetRiskMultiple = market == TradingMarketCatalog.NaturalGas
                ? NaturalGasMiniPositionPolicy.TargetRiskMultiple : 1m;
            var initialRisk = market.Code is "sensex" or "bank-nifty"
                ? MarketOptionPositionPolicy.InitialRiskForSavedPosition(
                    position.EntryPrice, position.InitialStopLoss, position.Target)
                : Math.Abs(position.Target - position.EntryPrice) / targetRiskMultiple;
            var positionRegime = MarketOptionPositionPolicy.Regime(position.Strategy);
            var exitPlan = MarketOptionPositionPolicy.ExitPlan(market.Code, position.Strategy);
            var favourable = position.Direction == Direction.Buy ? price - position.EntryPrice : position.EntryPrice - price;
            var favourableExtreme = price;
            var trailing = position.StopLoss;
            var previousStop = position.StopLoss;
            var expectedUnderlyingDirection = market.ExecuteOptions
                ? instrument.Type == InstrumentType.PutOption ? Direction.Sell : Direction.Buy
                : position.Direction;
            var india = TimeZoneInfo.ConvertTime(observedAt, IndiaTimeZone);
            var forced = market != TradingMarketCatalog.NaturalGas &&
                         TimeOnly.FromDateTime(india.DateTime) >= market.SessionEnd.AddMinutes(-15);
            var priorStopHit = position.Direction == Direction.Buy ? price <= previousStop : price >= previousStop;
            var invalidation = !protectionOnly && !priorStopHit && !forced &&
                               market is { Code: "sensex" or "bank-nifty" } &&
                               market.ExecuteOptions && !manualExitRequested &&
                               options.Value.UnderlyingTrendInvalidationEnabled
                ? await EvaluateUnderlyingInvalidationAsync(db, position.UnderlyingInstrumentId,
                    expectedUnderlyingDirection, position.OpenedAtUtc, observedAt, cancellationToken)
                : new UnderlyingTrendInvalidationResult(false, 0m, []);
            var differentiatedRunner = DifferentiatedRunnerPolicy.Eligible(
                options.Value.DifferentiatedRunnerEnabled && !protectionOnly &&
                !invalidation.ShouldExit && !priorStopHit &&
                market.Code is "sensex" or "bank-nifty" && options.Value.TargetExtensionEnabled &&
                DifferentiatedRunnerPolicy.AllowsTargetExtension(
                    MarketOptionPositionPolicy.AllowsTargetExtension(position.Strategy), invalidation.RunnerStructureIntact),
                position.Quantity, position.RemainingQuantity, position.PartialExitQuantity > 0,
                invalidation.RunnerStructureIntact, initialRisk > 0m ? favourable / initialRisk : 0m);
            if (market == TradingMarketCatalog.NaturalGas)
            {
                trailing = NaturalGasMiniPositionPolicy.ApplyTrailingStop(position.Direction,
                    position.EntryPrice, trailing, price, initialRisk);
            }
            else
            {
                if (!favourablePriceByPosition.TryGetValue(position.Id, out var favourablePrice))
                {
                    favourablePrice = position.EntryPrice;
                    if (!protectionOnly)
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
                }
                favourablePrice = position.Direction == Direction.Buy
                    ? Math.Max(favourablePrice, price) : Math.Min(favourablePrice, price);
                favourablePriceByPosition[position.Id] = favourablePrice;
                favourableExtreme = favourablePrice;
                trailing = MarketOptionPositionPolicy.ProtectedStop(position.Direction,
                    position.EntryPrice, initialRisk, favourablePrice, position.StopLoss,
                    position.RemainingQuantity, exitPlan, market.Exchange, differentiatedRunner);
            }
            var reversal = ConfirmedReversalResponse(market, invalidation, position.Direction,
                position.EntryPrice, price, trailing,
                options.Value.ProtectProfitableConfirmedReversals,
                options.Value.ReversalProfitLockFraction,
                instrument.TickSize > 0m
                    ? InstrumentPriceIncrement.FromGrowwInstrumentMaster(instrument.TickSize)
                    : .05m);
            var reversalExit = reversal.ShouldExit;
            var reversalProtection = reversal.ProtectProfit;
            trailing = reversal.Stop;
            position.Mark(price, trailing, observedAt);
            if (reversalExit || reversalProtection && position.StopLoss != previousStop)
                db.MarketStrategyAudits.Add(new(Guid.NewGuid(), market.Code,
                    position.UnderlyingInstrumentId, observedAt,
                    $"ConfirmedReversal:{position.Id:N}", invalidation.EvidenceScore,
                    JsonSerializer.Serialize(new { positionId = position.Id, price,
                        action = reversalExit ? "Exit" : "ProtectProfit",
                        previousStop, currentStop = position.StopLoss,
                        invalidation.Behaviour, invalidation.SupportingEvidence })));
            var sampleMinute = new DateTimeOffset(observedAt.Year, observedAt.Month, observedAt.Day,
                observedAt.Hour, observedAt.Minute,
                0, TimeSpan.Zero);
            var sampleOutcome = $"PositionSample:{position.Id:N}:{sampleMinute:yyyyMMddHHmm}";
            var recordSample = !protectionOnly && !await db.MarketStrategyAudits.AsNoTracking().AnyAsync(value =>
                value.Market == market.Code && value.Outcome == sampleOutcome, cancellationToken);
            if (recordSample)
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
                        initialStop = position.Direction == Direction.Buy
                            ? position.EntryPrice - initialRisk : position.EntryPrice + initialRisk,
                        previousStop, stop = position.StopLoss, position.Target,
                        livePnl, favourableR, protectionStage, differentiatedRunner, observedAtUtc = observedAt })));
            }
            var stopHit = position.Direction == Direction.Buy ? price <= position.StopLoss : price >= position.StopLoss;
            var targetHit = position.Direction == Direction.Buy ? price >= position.Target : price <= position.Target;
            // A breached stop takes precedence over continuation and partial exits.
            var continuationConfirmed = !protectionOnly && !manualExitRequested && !forced &&
                                        !stopHit && !reversalExit && !reversalProtection &&
                                        targetHit && options.Value.TargetExtensionEnabled &&
                                        DifferentiatedRunnerPolicy.AllowsTargetExtension(
                                            MarketOptionPositionPolicy.AllowsTargetExtension(position.Strategy),
                                            options.Value.DifferentiatedRunnerEnabled && invalidation.RunnerStructureIntact) &&
                                        (differentiatedRunner || await HasContinuationStructureAsync(db, position.UnderlyingInstrumentId,
                                            expectedUnderlyingDirection, cancellationToken));
            var failedSetup = false;
            var failedAssessment = new FailedSetupExitDecision(false, initialRisk <= 0m ? 0m :
                Math.Max(0m, (position.EntryPrice - price) / initialRisk),
                ["Not a weak adverse entry, or manual/stop/reversal exit already takes priority."]);
            if (!protectionOnly && !forced && market.ExecuteOptions && !manualExitRequested && !stopHit && !reversalExit &&
                FailedSetupExitPolicy.IsAdverseCandidate(position.EntryPrice, price,
                    initialRisk, favourableExtreme, position.OpenedAtUtc, observedAt))
            {
                var latestUnderlying = await db.MarketObservations.AsNoTracking()
                    .Where(value => value.InstrumentId == position.UnderlyingInstrumentId &&
                                    value.Source == "Groww")
                    .OrderByDescending(value => value.ReceivedAtUtc)
                    .FirstOrDefaultAsync(cancellationToken);
                failedAssessment = failedAssessment with { Reasons = ["Fresh underlying observation unavailable."] };
                if (latestUnderlying is not null && latestUnderlying.ReceivedAtUtc <= observedAt &&
                    observedAt - latestUnderlying.ReceivedAtUtc <=
                    TimeSpan.FromSeconds(marketOptions.Value.MaximumAgeSeconds))
                {
                    var intervalSeconds = marketOptions.Value.CandleIntervalSeconds;
                    var sessionStart = new DateTimeOffset(india.Date, india.Offset).ToUniversalTime();
                    var recentCandles = await db.Candles.AsNoTracking()
                        .Where(value => value.InstrumentId == position.UnderlyingInstrumentId &&
                                        value.Source == "Groww" && value.IntervalSeconds == intervalSeconds &&
                                        value.OpenTimeUtc >= sessionStart)
                        .OrderByDescending(value => value.OpenTimeUtc).Take(30)
                        .OrderBy(value => value.OpenTimeUtc).ToListAsync(cancellationToken);
                    var completedBars = recentCandles
                        .Where(value => value.OpenTimeUtc.AddSeconds(value.IntervalSeconds) <= observedAt)
                        .Select(value => new StrategyPriceBar(value.OpenTimeUtc,
                            value.Open, value.High, value.Low, value.Close)).ToArray();
                    var failedDecision = FailedSetupExitPolicy.EvaluateForMarket(market.Code,
                        position.Strategy, expectedUnderlyingDirection,
                        position.EntryPrice, price, initialRisk, favourableExtreme,
                        position.OpenedAtUtc, observedAt, latestUnderlying.Price, latestUnderlying.ReceivedAtUtc,
                        completedBars, intervalSeconds, marketOptions.Value.MaximumAgeSeconds);
                    failedAssessment = failedDecision;
                    failedSetup = failedDecision.ShouldExit;
                    if (failedSetup)
                        db.MarketStrategyAudits.Add(new(Guid.NewGuid(), market.Code,
                            position.UnderlyingInstrumentId, observedAt,
                            $"FailedSetupExit:{position.Id:N}", 0m,
                            FailedSetupExitAudit.Serialize(position.Id, price,
                                latestUnderlying.Price, failedDecision)));
                }
            }
            // Sample negative decisions too, at the existing once-per-minute cadence.
            // This distinguishes insufficient reversal evidence from missing/stale data.
            if (recordSample && market.Code is "sensex" or "bank-nifty")
                db.MarketStrategyAudits.Add(new(Guid.NewGuid(), market.Code,
                    position.UnderlyingInstrumentId, observedAt,
                    $"ExitAssessment:{position.Id:N}:{sampleMinute:yyyyMMddHHmm}", 0m,
                    JsonSerializer.Serialize(new { positionId = position.Id, price, initialRisk,
                        favourableExtreme, stopHit, reversalExit, manualExitRequested,
                        invalidation.ShouldExit, invalidation.Behaviour, invalidation.SupportingEvidence,
                        failedSetup = failedAssessment, observedAtUtc = observedAt })));
            var positionDecision = MarketOptionPositionPolicy.Decide(position.Direction,
                price, position.EntryPrice, position.StopLoss, position.Target, initialRisk,
                position.RemainingQuantity, instrument.LotSize, exitPlan, manualExitRequested,
                reversalExit, failedSetup, continuationConfirmed, forced,
                !protectionOnly && market.ExecuteOptions && options.Value.PartialProfitEnabled,
                position.PartialExitQuantity != 0);
            if (!protectionOnly && positionDecision.ExitReason is null &&
                BankNiftyConfirmedTrail.Applies(market.Code, position.Strategy) && position.InitialStopLoss is > 0m)
            {
                var trail = await EvaluateBankNiftyTrailAsync(db, position, quote, observedAt, initialRisk,
                    instrument.LotSize, expectedUnderlyingDirection, cancellationToken);
                if (trail.Exit)
                {
                    // Keep the existing protective order in place until the live
                    // reconciler takes over the terminal exit. Never submit here.
                    positionDecision = new(BankNiftyConfirmedTrail.ExitReason, 0);
                    price = quote.BidPrice!.Value;
                    wakeForTrailExit = true;
                }
            }
            if (positionDecision.PartialQuantity > 0)
            {
                var partialQuantity = positionDecision.PartialQuantity;
                var partialCosts = PaperTradingCostModel.CalculateOptionCharges([
                    new(PaperOptionTransactionSide.Buy, position.EntryPrice, partialQuantity),
                    new(PaperOptionTransactionSide.Sell, price, partialQuantity)], market.Exchange).Total;
                position.TakePartial(price, partialQuantity, partialCosts, observedAt);
                db.MarketStrategyAudits.Add(new(Guid.NewGuid(), market.Code,
                    position.UnderlyingInstrumentId, observedAt, $"PartialProfit:{position.Id:N}", 0m,
                    JsonSerializer.Serialize(new { positionId = position.Id, price, partialQuantity,
                        remainingQuantity = position.RemainingQuantity, partialCosts,
                        triggerR = exitPlan.PartialProfitR, regime = positionRegime.ToString() })));
            }
            if (position.StopLoss != previousStop || positionDecision.PartialQuantity > 0)
                db.MarketStrategyAudits.Add(new(Guid.NewGuid(), market.Code,
                    position.UnderlyingInstrumentId, observedAt,
                    $"TrailingStop:{position.Id:N}", 0m, JsonSerializer.Serialize(new
                    { positionId = position.Id, previousStop, newStop = position.StopLoss, price,
                        remainingQuantity = position.RemainingQuantity, observedAtUtc = observedAt })));
            if (positionDecision.ExitReason is { } exitReason)
            {
                var costs = market.ExecuteOptions
                    ? PaperTradingCostModel.CalculateOptionCharges([
                        new(PaperOptionTransactionSide.Buy, position.EntryPrice, position.RemainingQuantity),
                        new(PaperOptionTransactionSide.Sell, price, position.RemainingQuantity)], market.Exchange).Total
                    : decimal.Round((position.EntryPrice + price) * position.RemainingQuantity * 0.0005m, 2);
                position.Close(price, costs, exitReason, observedAt);
                favourablePriceByPosition.TryRemove(position.Id, out _);
                bankTrailSamples.TryRemove(position.Id, out _);
                var grossPnl = position.RealisedPnl + position.CostsPaid;
                db.MarketStrategyAudits.Add(new(Guid.NewGuid(), market.Code,
                    position.UnderlyingInstrumentId, observedAt, $"PositionClosed:{position.Id:N}", 0m,
                    JsonSerializer.Serialize(new { positionId = position.Id, exitPrice = price,
                        exitReason = position.Status, grossPnl, costs, netPnl = position.RealisedPnl,
                        targetContinuation = new { enabled = options.Value.TargetExtensionEnabled,
                            strategyEligible = MarketOptionPositionPolicy.AllowsTargetExtension(position.Strategy),
                            stopHit, reversalExit, reversalProtection, allowed = continuationConfirmed },
                        closedAtUtc = observedAt })));
            }
        }
        if (active.Count > 0) await db.SaveChangesAsync(cancellationToken);
        if (wakeForTrailExit) liveWakeSignal.Notify();
    }

    private async Task<BankNiftyTrailDecision> EvaluateBankNiftyTrailAsync(TradingDbContext db,
        MarketPaperPosition position, GrowwQuote quote, DateTimeOffset observedAt, decimal initialRisk,
        int lot, Direction direction, CancellationToken token)
    {
        var state = (bankTrailSamples.GetValueOrDefault(position.Id) ?? new BankNiftyTrailState()) with
            { Floor = position.BankNiftyTrailFloorNet };
        var book = quote.BidPrice is { } bid && quote.OfferPrice is { } ask && quote.OfferQuantity > 0 &&
            GrowwQuoteTimestamp.ToUtc(quote.LastTradeTimeEpochMilliseconds) is { } sourceAt
            ? new BankNiftyTrailBook(bid, ask, quote.BidQuantity ?? 0, observedAt, sourceAt) : null;
        BankNiftyTrailDecision Evaluate(BankNiftyTrailSpot? entry = null, BankNiftyTrailSpot? current = null) =>
            BankNiftyConfirmedTrail.Evaluate(state, position.OpenedAtUtc, observedAt, direction,
                position.EntryPrice, initialRisk, lot, position.RemainingQuantity, position.RealisedPnl, book, entry, current);

        var result = Evaluate();
        if (result.NeedsSpot)
        {
            var entry = await ReadSpot(position.OpenedAtUtc);
            var current = entry is null ? null : await ReadSpot(observedAt);
            result = Evaluate(entry, current);
        }
        bankTrailSamples[position.Id] = result.State with { Floor = null };
        var previousFloor = position.BankNiftyTrailFloorNet;
        if (result.State.Floor is { } floor && floor != previousFloor)
            position.RatchetBankNiftyTrailFloor(floor);
        if (result.Exit || position.BankNiftyTrailFloorNet != previousFloor)
            db.MarketStrategyAudits.Add(new(Guid.NewGuid(), position.Market, position.UnderlyingInstrumentId,
                observedAt, $"BankNiftyConfirmedTrail:{position.Id:N}", 0m,
                JsonSerializer.Serialize(new { positionId = position.Id, bid = book?.Bid, bidSize = book?.Size,
                    quoteSourceAt = book?.SourceAt, previousFloor, floorNet = position.BankNiftyTrailFloorNet,
                    triggered = result.Exit, position.RemainingQuantity })));
        return result;

        Task<BankNiftyTrailSpot?> ReadSpot(DateTimeOffset asOf)
        {
            var lower = asOf - BankNiftyConfirmedTrail.MaximumSpotAge;
            return db.MarketObservations.AsNoTracking()
                .Where(value => value.InstrumentId == position.UnderlyingInstrumentId && value.Source == "Groww" &&
                    value.ReceivedAtUtc >= lower && value.ReceivedAtUtc <= asOf && value.SourceTimestampUtc >= lower &&
                    value.SourceTimestampUtc <= value.ReceivedAtUtc)
                .OrderByDescending(value => value.ReceivedAtUtc).ThenByDescending(value => value.SourceTimestampUtc)
                .Select(value => new BankNiftyTrailSpot(value.Price, value.ReceivedAtUtc, value.SourceTimestampUtc))
                .FirstOrDefaultAsync(token);
        }
    }

    internal static (bool ShouldExit, bool ProtectProfit, decimal Stop) ConfirmedReversalResponse(
        TradingMarketDefinition market, UnderlyingTrendInvalidationResult invalidation,
        Direction optionDirection, decimal entry, decimal current, decimal existingStop,
        bool protectProfitable, decimal lockFraction, decimal tickSize = .05m) =>
        MarketOptionPositionPolicy.Reversal(market.Code, invalidation, optionDirection,
            entry, current, existingStop, protectProfitable, lockFraction, tickSize);

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
                        scheduledAtUtc = scheduled, observedAtUtc = timeProvider.GetUtcNow() })));
                await db.SaveChangesAsync(cancellationToken);
            }
        }
    }

    private async Task<bool> HasContinuationStructureAsync(TradingDbContext db, Guid underlyingInstrumentId,
        Direction direction, CancellationToken cancellationToken)
    {
        var observedAt = timeProvider.GetUtcNow();
        var latestUnderlyingAt = await db.MarketObservations.AsNoTracking()
            .Where(value => value.InstrumentId == underlyingInstrumentId && value.Source == "Groww")
            .OrderByDescending(value => value.ReceivedAtUtc)
            .Select(value => (DateTimeOffset?)value.ReceivedAtUtc).FirstOrDefaultAsync(cancellationToken);
        var interval = marketOptions.Value.CandleIntervalSeconds;
        var completedBefore = observedAt.AddSeconds(-interval);
        var candles = await db.Candles.AsNoTracking()
            .Where(value => value.InstrumentId == underlyingInstrumentId &&
                            value.IntervalSeconds == interval && value.OpenTimeUtc <= completedBefore &&
                            value.Source == "Groww")
            .OrderByDescending(value => value.OpenTimeUtc)
            .Take(21)
            .OrderBy(value => value.OpenTimeUtc)
            .ToListAsync(cancellationToken);
        return MarketOptionPositionPolicy.FreshContinuation(candles.Select(value =>
            new StrategyPriceBar(value.OpenTimeUtc, value.Open, value.High,
                value.Low, value.Close)).ToArray(), direction, observedAt, latestUnderlyingAt,
            interval, marketOptions.Value.MaximumAgeSeconds);
    }

    private async Task<UnderlyingTrendInvalidationResult> EvaluateUnderlyingInvalidationAsync(
        TradingDbContext db, Guid underlyingInstrumentId, Direction originalDirection,
        DateTimeOffset openedAtUtc, DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken)
    {
        var latestUnderlyingAtUtc = await db.MarketObservations.AsNoTracking()
            .Where(value => value.InstrumentId == underlyingInstrumentId && value.Source == "Groww")
            .OrderByDescending(value => value.ReceivedAtUtc)
            .Select(value => (DateTimeOffset?)value.ReceivedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        if (latestUnderlyingAtUtc is null || latestUnderlyingAtUtc > observedAtUtc ||
            observedAtUtc - latestUnderlyingAtUtc >
                TimeSpan.FromSeconds(marketOptions.Value.MaximumAgeSeconds))
            return new(false, 0m, ["Fresh underlying data is unavailable; reversal exits fail closed."]);

        var interval = marketOptions.Value.CandleIntervalSeconds;
        var india = TimeZoneInfo.ConvertTime(observedAtUtc, IndiaTimeZone);
        var sessionStartUtc = new DateTimeOffset(india.Date, india.Offset).ToUniversalTime();
        var candles = await db.Candles.AsNoTracking()
            .Where(value => value.InstrumentId == underlyingInstrumentId &&
                            value.IntervalSeconds == interval && value.Source == "Groww" &&
                            value.OpenTimeUtc >= sessionStartUtc &&
                            value.OpenTimeUtc <= observedAtUtc.AddSeconds(-interval))
            .OrderByDescending(value => value.OpenTimeUtc)
            .Take(30)
            .OrderBy(value => value.OpenTimeUtc)
            .ToListAsync(cancellationToken);
        var bars = candles.Select(value => new StrategyPriceBar(value.OpenTimeUtc,
            value.Open, value.High, value.Low, value.Close)).ToArray();
        return MarketOptionPositionPolicy.Invalidation(bars, originalDirection,
            openedAtUtc, observedAtUtc, interval, marketOptions.Value.MaximumAgeSeconds,
            options.Value.MinimumReversalStructureStrength,
            options.Value.RequiredReversalEvidenceCount);
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
        if (market == TradingMarketCatalog.Sensex || market == TradingMarketCatalog.BankNifty)
        {
            var candidates = await db.Instruments.AsNoTracking().Where(value => value.IsActive &&
                value.Exchange == market.Exchange && value.Segment == InstrumentSegment.FuturesAndOptions &&
                value.TradingSymbol.StartsWith(market.ExecutionUnderlying) && value.ExpiryDate >= today &&
                value.ExpiryDate <= today.AddDays(45) && value.StrikePrice != null &&
                (value.Type == InstrumentType.CallOption || value.Type == InstrumentType.PutOption)).ToArrayAsync(cancellationToken);
            var selected = RegularOptionContractSelector.Select(candidates.Select(value =>
                new NiftyOptionContractCandidate(value.Id, value.TradingSymbol, value.Type,
                    value.ExpiryDate!.Value, value.StrikePrice!.Value, value.LotSize, value.TickSize)).ToArray(),
                market.ExecutionUnderlying, today, direction, price, preferredInTheMoneySteps);
            return selected is null ? null : candidates.Single(value => value.Id == selected.InstrumentId);
        }
        var strikeStep = market == TradingMarketCatalog.Sensex ? 100m :
            market == TradingMarketCatalog.Bankex ? 100m :
            market == TradingMarketCatalog.BankNifty ? 100m : 50m;
        var desiredStrike = direction == Direction.Buy
            ? price - preferredInTheMoneySteps * strikeStep
            : price + preferredInTheMoneySteps * strikeStep;
        var lastEligibleExpiry = today.AddDays(OptionExpiryHorizonDays(market));
        var instruments = await db.Instruments.AsNoTracking().Where(value => value.Exchange == market.Exchange &&
                value.Segment == InstrumentSegment.FuturesAndOptions && value.IsActive && value.ExpiryDate >= today &&
                value.ExpiryDate <= lastEligibleExpiry && value.StrikePrice != null &&
                (direction == Direction.Buy ? value.Type == InstrumentType.CallOption : value.Type == InstrumentType.PutOption) &&
                value.TradingSymbol.StartsWith(market.ExecutionUnderlying))
            .OrderBy(value => value.ExpiryDate).ThenBy(value => Math.Abs(value.StrikePrice!.Value - desiredStrike))
            .FirstOrDefaultAsync(cancellationToken);
        return instruments;
    }

    internal static int OptionExpiryHorizonDays(TradingMarketDefinition market) =>
        market is { Code: "bank-nifty" or "bankex" } ? 45 : 10;

    internal static DateOnly RegularExecutionSearchDate(TradingMarketDefinition market,
        DateOnly sessionDate, DateOnly? selectedExpiry) =>
        market == TradingMarketCatalog.Sensex && selectedExpiry == sessionDate
            ? sessionDate.AddDays(1) : sessionDate;

    internal static DateOnly BankRegularOptionStartDate(DateOnly sessionDate) =>
        sessionDate.AddDays(1);

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

    private static async Task AuditIfDueAsync(TradingDbContext db, TradingMarketDefinition market, Guid instrumentId,
        DateTimeOffset candleTime, MarketDecision decision, CancellationToken cancellationToken)
    {
        if (await db.MarketStrategyAudits.AsNoTracking().AnyAsync(value => value.Market == market.Code &&
            value.UnderlyingInstrumentId == instrumentId && value.Outcome == "NoSignal" &&
            value.CandleTimeUtc == candleTime, cancellationToken)) return;
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
