using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using TradingSystem.Application.Auditing;
using TradingSystem.Application.Broker;
using TradingSystem.Application.Execution;
using TradingSystem.Application.MarketData;
using TradingSystem.Application.Regime;
using TradingSystem.Application.Risk;
using TradingSystem.Application.Strategies;
using TradingSystem.Domain;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.MarketData;
using TradingSystem.Infrastructure.Diagnostics;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.Infrastructure.Execution;

internal sealed partial class AutomatedPaperTradingService(
    IServiceScopeFactory scopeFactory,
    IBrokerGateway broker,
    IPaperBrokerControl paperControl,
    PaperAutomationState state,
    IOptions<AutomatedPaperTradingOptions> options,
    IOptions<MarketDataOptions> marketOptions,
    TimeProvider timeProvider,
    TradingEngineEvidenceRecorder evidenceRecorder,
    ILogger<AutomatedPaperTradingService> logger) : BackgroundService
{
    private static readonly TimeZoneInfo IndiaTimeZone = FindIndiaTimeZone();
    private DateTimeOffset? lastEvaluatedCandleUtc;
    private string cyclePhase = "Startup";
    private Dictionary<string, object?> cycleInputs = [];
    private readonly Dictionary<Guid, decimal> favourablePriceBySignal = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            state.Record("Disabled", false, "Automated paper trading is disabled by server configuration.");
            await RecordCycleEvidenceAsync(stoppingToken);
            return;
        }

        if (broker.Mode != TradingMode.Paper)
            throw new InvalidOperationException("Automated paper trading can run only in Paper mode.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                var cause = exception.GetBaseException();
                var failureCode = cause is PostgresException postgres
                    ? $"{cause.GetType().Name}/{postgres.SqlState}"
                    : cause.GetType().Name;
                state.Record("Faulted", false,
                    $"Automation cycle failed ({failureCode}); new entries are blocked.");
                LogCycleFailed(logger, exception);
            }

            await RecordCycleEvidenceAsync(stoppingToken);

            await Task.Delay(TimeSpan.FromSeconds(options.Value.EvaluationIntervalSeconds), stoppingToken);
        }
    }

    private async Task RecordCycleEvidenceAsync(CancellationToken cancellationToken)
    {
        var snapshot = state.GetCurrent();
        var local = TimeZoneInfo.ConvertTime(snapshot.ObservedAtUtc, IndiaTimeZone);
        if (snapshot.Status != "Disabled" &&
            (local.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ||
             TimeOnly.FromDateTime(local.DateTime) < new TimeOnly(9, 15) ||
             TimeOnly.FromDateTime(local.DateTime) > new TimeOnly(15, 15))) return;
        await evidenceRecorder.RecordAsync("nifty", "regular", cyclePhase,
            snapshot.Status, snapshot.Message, snapshot.ObservedAtUtc,
            new { inputs = cycleInputs, automation = snapshot }, cancellationToken);
    }

    private async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        cyclePhase = "LoadConfigurationAndInstrument";
        cycleInputs = [];
        cycleInputs["configuration"] = new
        {
            options.Value.Enabled, options.Value.MinimumSignalCandles,
            options.Value.MinimumFuturesConfirmationCandles,
            options.Value.EntryWindowStart, options.Value.EntryCutoff,
            marketOptions.Value.CandleIntervalSeconds,
            marketOptions.Value.MaximumAgeSeconds
        };
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var configuredLots = (await scope.ServiceProvider
            .GetRequiredService<ITradingLotConfigurationService>()
            .GetAsync(cancellationToken)).NiftyLots;
        cycleInputs["configuredLots"] = configuredLots;
        var live = scope.ServiceProvider.GetRequiredService<IOptions<LiveNiftyOptions>>().Value;
        var instrument = await db.Instruments.AsNoTracking().SingleOrDefaultAsync(value =>
            value.Exchange == live.Exchange && value.TradingSymbol == live.TradingSymbol &&
            value.Segment == InstrumentSegment.Cash && value.IsActive, cancellationToken);
        if (instrument is null)
        {
            state.Record("Blocked", false, "Nifty instrument is not synchronised.");
            return;
        }
        cycleInputs["underlyingInstrumentId"] = instrument.Id;
        cyclePhase = "SessionAndPortfolioRecovery";

        try
        {
            await CaptureExitResearchAsync(scope.ServiceProvider, db, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogExitResearchCaptureFailed(logger, exception);
        }

        var now = timeProvider.GetUtcNow();
        var indiaNow = TimeZoneInfo.ConvertTime(now, IndiaTimeZone);
        if (indiaNow.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            state.Record("MarketClosed", false, "Waiting for the next NSE weekday session.");
            return;
        }

        var sessionStart = ToUtc(indiaNow.Date, new TimeOnly(9, 15));
        var sessionEnd = ToUtc(indiaNow.Date, new TimeOnly(15, 30));
        var sessionSignals = await LoadSessionSignalsAsync(db, instrument.Id, sessionStart, sessionEnd, cancellationToken);
        // Recovery must include unresolved positions opened on an earlier session. Limiting this
        // query to today would orphan any explicitly approved carry-forward paper position.
        var recoveryStart = sessionStart.AddDays(-options.Value.PositionRecoveryLookbackDays);
        var recoverySignals = await LoadSessionSignalsAsync(db, instrument.Id, recoveryStart, sessionEnd,
            cancellationToken);
        var killSwitch = await db.ApplicationSettings.AsNoTracking().Where(value =>
                value.Mode == TradingMode.Paper && value.Key == "EmergencyKillSwitch")
            .Select(value => value.ValueJson).SingleOrDefaultAsync(cancellationToken);
        var killSwitchActive = bool.TryParse(killSwitch, out var active) && active;
        cycleInputs["killSwitchActive"] = killSwitchActive;
        var dailyLossOverrideJson = await db.ApplicationSettings.AsNoTracking().Where(value =>
                value.Mode == TradingMode.Paper && value.Key == EfPaperDailyLossOverrideService.Key)
            .Select(value => value.ValueJson).SingleOrDefaultAsync(cancellationToken);
        var dailyLossLimitOverridden = !options.Value.EnforceDailyLossLimit ||
            EfPaperDailyLossOverrideService.IsActiveForDate(
                dailyLossOverrideJson, DateOnly.FromDateTime(indiaNow.Date));
        await UpdateReadinessAsync(db, instrument.Id, sessionStart, sessionEnd, now, indiaNow,
            killSwitchActive, cancellationToken);
        var tradeState = await RebuildTradeStateAsync(db, recoverySignals, sessionStart, sessionEnd,
            cancellationToken);
        cyclePhase = "PaperReconciliation";
        var reconciliation = await broker.ReconcileAsync(
            tradeState.OpenPositions.Select(open => new ExpectedBrokerPosition(open.Entry.InstrumentId,
                open.Entry.Direction, open.RemainingQuantity)).ToArray(), cancellationToken);
        if (!reconciliation.TradingPermitted)
        {
            RecordPortfolioRisk(state, tradeState, options.Value.MaximumDailyLoss, false);
            state.Record("ReconciliationRequired", false,
                $"Paper position mismatch: {string.Join(" ", reconciliation.Mismatches)}",
                tradeState.TradesToday, tradeState.RealisedPnl);
            return;
        }

        var latest = await db.MarketObservations.AsNoTracking()
            .Where(value => value.InstrumentId == instrument.Id && value.Source == "Groww")
            .OrderByDescending(value => value.SourceTimestampUtc).FirstOrDefaultAsync(cancellationToken);
        var fresh = latest is not null && now - latest.ReceivedAtUtc <=
            TimeSpan.FromSeconds(marketOptions.Value.MaximumAgeSeconds);
        cycleInputs["spotSourceAtUtc"] = latest?.SourceTimestampUtc;
        cycleInputs["spotReceivedAtUtc"] = latest?.ReceivedAtUtc;
        cycleInputs["maximumQuoteAgeSeconds"] = marketOptions.Value.MaximumAgeSeconds;
        cycleInputs["spotFresh"] = fresh;
        cyclePhase = "PositionManagement";

        state.SynchronizeActivePositions(tradeState.OpenPositions.Select(value => value.Signal.SignalId));

        var openPositionMonitoringFailed = false;
        if (tradeState.OpenPositions.Count > 0)
        {
            foreach (var open in tradeState.OpenPositions)
            {
                var optionInstrument = await db.Instruments.AsNoTracking()
                    .SingleOrDefaultAsync(value => value.Id == open.Entry.InstrumentId,
                        cancellationToken);
                if (optionInstrument is null)
                {
                    state.Record("PositionUnmonitored", false,
                        "An open paper option position exists but its instrument metadata is unavailable.",
                        tradeState.TradesToday, tradeState.RealisedPnl, 0m,
                        open.Signal.SignalId, open.Entry.Direction.ToString(),
                        open.Entry.FilledQuantity, open.Entry.AverageFillPrice,
                        open.StopLoss, open.Target);
                    return;
                }
                if (optionInstrument.Type is not (InstrumentType.CallOption or InstrumentType.PutOption))
                {
                    state.Record("ReconciliationRequired", false,
                        "A legacy non-option paper position was detected. It will not be monitored or reused as a Nifty option trade.",
                        tradeState.TradesToday, tradeState.RealisedPnl, 0m,
                        open.Signal.SignalId, open.Entry.Direction.ToString(),
                        open.Entry.FilledQuantity, open.Entry.AverageFillPrice,
                        open.StopLoss, open.Target);
                    return;
                }

                GrowwQuote optionQuote;
                try
                {
                    var groww = scope.ServiceProvider.GetRequiredService<IGrowwReadOnlyGateway>();
                    optionQuote = await groww.GetQuoteAsync(new GrowwQuoteRequest(
                        optionInstrument.Exchange, "FNO", optionInstrument.TradingSymbol), cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    state.RecordPositionMark(open.Signal.SignalId, null, null, null, false);
                    LogPositionQuoteFailed(logger, optionInstrument.TradingSymbol, exception);
                    openPositionMonitoringFailed = true;
                    continue;
                }

                // LTP is the position's market mark shown on the dashboard. The bid remains the
                // conservative executable value used for exits and protective-price evaluation.
                var displayPrice = optionQuote.LastPrice > 0
                    ? optionQuote.LastPrice
                    : optionQuote.BidPrice;
                var executablePrice = optionQuote.BidPrice is > 0
                    ? optionQuote.BidPrice.Value
                    : optionQuote.LastPrice;
                var optionFresh = displayPrice is > 0 && executablePrice > 0;
                var positionMultiplier = open.Entry.Direction == Direction.Buy ? 1m : -1m;
                var unrealised = displayPrice is > 0 && open.Entry.AverageFillPrice is { } entryPrice
                    ? (displayPrice.Value - entryPrice) * open.RemainingQuantity * positionMultiplier
                    : (decimal?)null;
                state.RecordPositionMark(open.Signal.SignalId, displayPrice, executablePrice,
                    unrealised, optionFresh);
                var monitoringBlocked = await ManageOpenPositionAsync(scope.ServiceProvider, tradeState,
                    open, executablePrice, indiaNow.TimeOfDay, optionFresh, fresh, killSwitchActive,
                    optionInstrument, instrument.Id, sessionStart, sessionEnd, cancellationToken);
                openPositionMonitoringFailed |= monitoringBlocked;
            }
        }

        if (openPositionMonitoringFailed)
        {
            RecordPortfolioRisk(state, tradeState, options.Value.MaximumDailyLoss, true);
            state.Record("PositionUnmonitored", false,
                "At least one active option quote is unavailable; all other positions remain monitored and new entries are blocked.",
                tradeState.TradesToday, tradeState.RealisedPnl);
            return;
        }

        RecordPortfolioRisk(state, tradeState, options.Value.MaximumDailyLoss, true);
        cyclePhase = "EntryPrerequisites";

        if (killSwitchActive)
        {
            state.Record("KillSwitch", false, "Emergency kill switch is active; new entries are blocked.",
                tradeState.TradesToday, tradeState.RealisedPnl);
            return;
        }

        if (!fresh)
        {
            state.Record("DataStale", false, "Fresh Groww market data is required for new entries.",
                tradeState.TradesToday, tradeState.RealisedPnl);
            return;
        }
        var currentPrice = latest!.Price;

        var openingRangeEnd = ParseTime(options.Value.OpeningRangeEnd);
        var entryWindowStart = ParseTime(options.Value.EntryWindowStart);
        var entryCutoff = ParseTime(options.Value.EntryCutoff);
        var localTime = TimeOnly.FromTimeSpan(indiaNow.TimeOfDay);
        cycleInputs["entryWindowStart"] = entryWindowStart;
        cycleInputs["entryCutoff"] = entryCutoff;
        if (!IndexEntryWindowPolicy.AllowsEntry(localTime, entryWindowStart, entryCutoff))
        {
            state.Record(localTime < entryWindowStart ? "WarmingUp" : "EntryCutoff", false,
                localTime < entryWindowStart ? "Waiting for the paper entry window." :
                "New-entry cutoff reached; monitoring only.", tradeState.TradesToday, tradeState.RealisedPnl);
            return;
        }

        var candles = await db.Candles.AsNoTracking().Where(value =>
                value.InstrumentId == instrument.Id && value.Source == "Groww" &&
                value.IntervalSeconds == marketOptions.Value.CandleIntervalSeconds &&
                value.OpenTimeUtc >= sessionStart && value.OpenTimeUtc < sessionEnd)
            .OrderBy(value => value.OpenTimeUtc).ToListAsync(cancellationToken);
        cycleInputs["spotCandleCount"] = candles.Count;
        cycleInputs["minimumSpotCandles"] = options.Value.MinimumSignalCandles;
        if (candles.Count < options.Value.MinimumSignalCandles)
        {
            state.Record("WarmingUp", false,
                $"Waiting for minimum pattern data: {candles.Count}/{options.Value.MinimumSignalCandles} completed candles.",
                tradeState.TradesToday, tradeState.RealisedPnl);
            return;
        }

        var sessionDate = DateOnly.FromDateTime(indiaNow.Date);
        cyclePhase = "FuturesConfirmation";
        var confirmationFuture = await db.Instruments.AsNoTracking().Where(value =>
                value.Exchange == "NSE" && value.Type == InstrumentType.Future &&
                value.TradingSymbol.StartsWith("NIFTY") && value.IsActive &&
                value.ExpiryDate >= sessionDate)
            .OrderBy(value => value.ExpiryDate).ThenBy(value => value.TradingSymbol)
            .FirstOrDefaultAsync(cancellationToken);
        if (confirmationFuture is null)
        {
            state.Record("ConfirmationUnavailable", false,
                "The Nifty futures confirmation instrument is not synchronised.",
                tradeState.TradesToday, tradeState.RealisedPnl);
            return;
        }
        cycleInputs["futureSymbol"] = confirmationFuture.TradingSymbol;
        cycleInputs["futureInstrumentId"] = confirmationFuture.Id;
        var confirmationCandles = await db.Candles.AsNoTracking().Where(value =>
                value.InstrumentId == confirmationFuture.Id && value.Source == "Groww" &&
                value.IntervalSeconds == marketOptions.Value.CandleIntervalSeconds &&
                value.OpenTimeUtc >= sessionStart && value.OpenTimeUtc < sessionEnd)
            .OrderBy(value => value.OpenTimeUtc).ToListAsync(cancellationToken);
        cycleInputs["futureCandleCount"] = confirmationCandles.Count;
        cycleInputs["minimumFutureCandles"] = options.Value.MinimumFuturesConfirmationCandles;
        if (confirmationCandles.Count < options.Value.MinimumFuturesConfirmationCandles)
        {
            state.Record("WarmingUp", false,
                $"Waiting for minimum Nifty futures data: {confirmationCandles.Count}/{options.Value.MinimumFuturesConfirmationCandles} completed candles.",
                tradeState.TradesToday, tradeState.RealisedPnl);
            return;
        }

        var latestCandle = candles[^1];
        cycleInputs["lastSpotCandleAtUtc"] = latestCandle.OpenTimeUtc;
        if (lastEvaluatedCandleUtc == latestCandle.OpenTimeUtc)
        {
            state.Record("Scanning", true, "Latest completed candle evaluated; waiting for the next candle.",
                tradeState.TradesToday, tradeState.RealisedPnl);
            return;
        }
        lastEvaluatedCandleUtc = latestCandle.OpenTimeUtc;
        var candleDecisionTime = latestCandle.OpenTimeUtc.AddSeconds(latestCandle.IntervalSeconds);
        if (sessionSignals.Any(value => value.MarketDataTimestampUtc == candleDecisionTime))
        {
            state.Record("Scanning", true,
                "This completed candle already has a durable signal decision; duplicate evaluation was suppressed.",
                tradeState.TradesToday, tradeState.RealisedPnl);
            return;
        }

        cyclePhase = "PreviousSessionAndAlignedFeatures";
        var previousCandidates = await db.Candles.AsNoTracking().Where(value =>
                value.InstrumentId == instrument.Id && value.Source == "Groww" &&
                value.IntervalSeconds == marketOptions.Value.CandleIntervalSeconds &&
                value.OpenTimeUtc < sessionStart)
            .OrderByDescending(value => value.OpenTimeUtc).Take(2500)
            .OrderBy(value => value.OpenTimeUtc).ToListAsync(cancellationToken);
        var previousSessionCandles = previousCandidates
            .GroupBy(value => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value.OpenTimeUtc,
                IndiaTimeZone).Date))
            .OrderByDescending(group => group.Key).Take(5)
            .OrderBy(group => group.Key).SelectMany(group => group.OrderBy(value => value.OpenTimeUtc))
            .ToArray();
        var previousClose = previousSessionCandles.LastOrDefault()?.Close;
        cycleInputs["previousClose"] = previousClose;
        if (previousClose is null)
        {
            state.Record("WarmingUp", false,
                "Previous-session close is unavailable; one persisted session is required.",
                tradeState.TradesToday, tradeState.RealisedPnl);
            return;
        }

        var alignedConfirmation = confirmationCandles
            .Where(value => value.OpenTimeUtc <= latestCandle.OpenTimeUtc).ToArray();
        cycleInputs["alignedFutureCandles"] = alignedConfirmation.Length;
        cycleInputs["alignedFutureVolume"] = alignedConfirmation.Sum(value => value.Volume);
        if (alignedConfirmation.Length < 2)
        {
            state.Record("WarmingUp", false,
                "Nifty futures candles are not yet aligned with the Nifty signal candle.",
                tradeState.TradesToday, tradeState.RealisedPnl);
            return;
        }
        var features = NiftySignalFeaturePolicy.Calculate(
            candles.Select(value => new StrategyPriceBar(value.OpenTimeUtc,
                value.Open, value.High, value.Low, value.Close)).ToArray(),
            alignedConfirmation.Select(value => new StrategyPriceBar(
                value.OpenTimeUtc, value.Open, value.High, value.Low,
                value.Close, value.Volume)).ToArray(), openingRangeEnd);
        if (features is null)
        {
            state.Record("DataIncomplete", false,
                "Aligned spot/futures volume or opening-range data is unavailable; the strategy fails closed.",
                tradeState.TradesToday, tradeState.RealisedPnl);
            return;
        }
        var relativeVolume = features.RelativeFuturesVolume;
        cyclePhase = "StrategyEvaluation";
        cycleInputs["relativeFuturesVolume"] = relativeVolume;
        var vwap = features.Vwap;
        var fast = features.FastEma;
        var slow = features.SlowEma;
        var atr = features.AtrPercent;
        var openingRangeHigh = features.OpeningRangeHigh;
        var openingRangeLow = features.OpeningRangeLow;
        var sessionId = DeterministicSessionId(DateOnly.FromDateTime(indiaNow.Date));
        var tradesByStrategy = await db.StrategyEvaluations.AsNoTracking()
            .Where(value => value.CandleTimeUtc >= sessionStart && value.CandleTimeUtc < sessionEnd &&
                            value.Outcome == "PaperPositionOpened")
            .GroupBy(value => value.StrategyCode)
            .ToDictionaryAsync(group => group.Key, group => group.Count(), cancellationToken);
        var regimeService = scope.ServiceProvider.GetRequiredService<MarketRegimeService>();
        var regime = await regimeService.EvaluateAsync(new MarketRegimeInput(
            sessionId, latestCandle.OpenTimeUtc, latestCandle.Close, previousClose.Value,
            candles[0].Open, openingRangeHigh, openingRangeLow,
            vwap, fast, slow, atr, relativeVolume, 1m, true), cancellationToken);
        var strategy = scope.ServiceProvider.GetRequiredService<ITradingStrategy>();
        // Rejected/unfilled signals must not consume the strategy's entry cooldown.
        var lastCompletedSignalTime = await (from result in db.PaperTradeResults.AsNoTracking()
            join priorSignal in db.Signals.AsNoTracking() on result.SignalId equals priorSignal.Id
            where priorSignal.InstrumentId == instrument.Id &&
                  priorSignal.MarketDataTimestampUtc >= sessionStart &&
                  priorSignal.MarketDataTimestampUtc < sessionEnd
            orderby priorSignal.MarketDataTimestampUtc descending
            select (DateTimeOffset?)priorSignal.MarketDataTimestampUtc).FirstOrDefaultAsync(cancellationToken);
        var strategyContext = new StrategyEvaluationContext(instrument.Id,
            candleDecisionTime, latestCandle.Close,
            openingRangeHigh, openingRangeLow, relativeVolume,
            regime.Regime, regime.DirectionalBias, regime.Confidence, regime.TradingPermitted, true,
            lastCompletedSignalTime,
            tradeState.TradesToday)
        {
            Vwap = vwap,
            FastEma = fast,
            SlowEma = slow,
            AtrPercent = atr,
            RecentCandles = candles.TakeLast(30).Select(value => new StrategyPriceBar(
                value.OpenTimeUtc, value.Open, value.High, value.Low, value.Close)).ToArray(),
            TradesByStrategyToday = tradesByStrategy
        };
        strategyContext = strategyContext with
        {
            MarketStructure = MarketStructureAnalyzer.Analyze(strategyContext.RecentCandles)
        };
        var shadowStructure = NiftySignalFeaturePolicy.EntryStructure(strategyContext);
        var strategyEvaluation = strategy.EvaluateDetailed(strategyContext);
        var signal = strategyEvaluation.Signal;
        var preEntryStateV2 = MarketStateRouterV2.Analyze(strategyContext.RecentCandles,
            vwap, fast, slow);
        // Observation-only challenger. It records a persistent multi-candle thesis but
        // cannot create or modify a StrategySignal, paper position, risk decision or order.
        await DirectionalThesisShadowAudit.RecordAsync(db, "nifty", instrument.Id,
            candleDecisionTime, strategyContext.RecentCandles, vwap,
            openingRangeHigh, openingRangeLow, cancellationToken);
        var marketStateV2 = preEntryStateV2;
        var multiTimeframeV2 = MultiTimeframeDecisionPolicy.Analyze(
            candles.Select(value => new StrategyPriceBar(value.OpenTimeUtc, value.Open,
                value.High, value.Low, value.Close)).ToArray(), signal?.Direction,
            openingRangeHigh, openingRangeLow);
        if (!await db.MarketStrategyAudits.AsNoTracking().AnyAsync(value =>
                value.Market == "nifty" && value.Outcome == "DecisionSnapshot:v2" &&
                value.UnderlyingInstrumentId == instrument.Id &&
                value.CandleTimeUtc == candleDecisionTime, cancellationToken))
        {
            var snapshot = new DecisionFeatureSnapshotV2("decision-dataset-v2", "nifty",
                candleDecisionTime, latestCandle.Close,
                signal?.StrategyId ?? strategy.StrategyId, signal?.Direction,
                signal?.Confidence ?? 0m, marketStateV2,
                signal is null ? "Scanning" : "Candidate",
                vwap, fast, slow, atr, relativeVolume, openingRangeHigh, openingRangeLow,
                signal?.ProposedStopLoss, signal?.ProposedTarget,
                [.. (signal?.SupportingReasons ?? []), .. multiTimeframeV2.Evidence],
                strategyEvaluation.FailedConditions);
            db.MarketStrategyAudits.Add(new(Guid.NewGuid(), "nifty", instrument.Id,
                candleDecisionTime, "DecisionSnapshot:v2", signal?.Confidence ?? 0m,
                JsonSerializer.Serialize(snapshot)));
            await db.SaveChangesAsync(cancellationToken);
        }
        if (signal is null)
        {
            await PersistStrategyEvaluationAsync(db, strategy, instrument.Id, candleDecisionTime,
                latestCandle.Close, openingRangeHigh, openingRangeLow, vwap, fast, slow, atr,
                relativeVolume, regime, "NoSignal", strategyEvaluation.FailedConditions,
                null, null, null, cancellationToken, shadowStructure);
            state.Record("Scanning", true,
                $"Scanning each completed candle across the price-action portfolio. " +
                $"{string.Join(" ", strategyEvaluation.FailedConditions)} " +
                "The no-trade reason is stored for this completed candle.",
                tradeState.TradesToday, tradeState.RealisedPnl);
            return;
        }

        var firstEntryTiming = FirstEntryTimingPolicy.Evaluate(strategyContext.RecentCandles,
            signal.Direction);
        if (!firstEntryTiming.Permitted)
        {
            await PersistStrategyEvaluationAsync(db, strategy, instrument.Id, candleDecisionTime,
                latestCandle.Close, openingRangeHigh, openingRangeLow, vwap, fast, slow, atr,
                relativeVolume, regime, "EntryTimingRejected", firstEntryTiming.Reasons,
                signal, null, null, cancellationToken, shadowStructure);
            state.Record("EntryTimingRejected", true, string.Join(" ", firstEntryTiming.Reasons),
                tradeState.TradesToday, tradeState.RealisedPnl, signalId: signal.SignalId,
                direction: signal.Direction.ToString());
            return;
        }

        // Preserve the Nifty strategy itself, but prevent the same completed move from
        // immediately reopening after an exit. A clock-only cooldown cannot prove that
        // a new setup exists, so require a completed pullback/rejection reset instead.
        var priorSameDirectionExit = await (from result in db.PaperTradeResults.AsNoTracking()
            join priorSignal in db.Signals.AsNoTracking() on result.SignalId equals priorSignal.Id
            where priorSignal.InstrumentId == instrument.Id &&
                  priorSignal.Direction == signal.Direction &&
                  result.ClosedAtUtc >= sessionStart && result.ClosedAtUtc < sessionEnd
            orderby result.ClosedAtUtc descending
            select (DateTimeOffset?)result.ClosedAtUtc).FirstOrDefaultAsync(cancellationToken);
        var persistentDirectionalContext = ProfessionalTradeFramework.Map(marketStateV2) ==
                                           ProfessionalTradeRegime.Directional;
        var reentryDecision = StructuralReentryPolicy.Evaluate(strategyContext.RecentCandles
                .Where(bar => bar.OpenTimeUtc.AddSeconds(marketOptions.Value.CandleIntervalSeconds) <= now).ToArray(),
            signal.Direction, priorSameDirectionExit, persistentDirectionalContext);
        // A persistent thesis is not fresh post-exit evidence. Continuation bases remain eligible.
        if (!reentryDecision.Permitted)
        {
            await PersistStrategyEvaluationAsync(db, strategy, instrument.Id, candleDecisionTime,
                latestCandle.Close, openingRangeHigh, openingRangeLow, vwap, fast, slow, atr,
                relativeVolume, regime, "StructuralReentryRejected", reentryDecision.Reasons,
                signal, null, null, cancellationToken, shadowStructure);
            state.Record("StructuralReentryRejected", true,
                string.Join(" ", reentryDecision.Reasons), tradeState.TradesToday,
                tradeState.RealisedPnl, signalId: signal.SignalId,
                direction: signal.Direction.ToString());
            return;
        }

        shadowStructure = MarketStructureQualityAnalyzer.Analyze(
            strategyContext.RecentCandles, signal.Direction, strategyContext.CurrentPrice,
            strategyContext.Vwap, strategyContext.AtrPercent, strategyContext.OpeningRangeHigh,
            strategyContext.OpeningRangeLow, Math.Abs(signal.ProposedEntry - signal.ProposedStopLoss));

        var fullSessionBars = candles
            .Where(value => value.OpenTimeUtc.AddSeconds(value.IntervalSeconds) <= now)
            .Select(value => new StrategyPriceBar(value.OpenTimeUtc,
                value.Open, value.High, value.Low, value.Close)).ToArray();
        var preQuoteGate = NiftyPreQuoteEntryGatePolicy.Evaluate(
            TimeOnly.FromDateTime(indiaNow.DateTime), fullSessionBars,
            previousSessionCandles.Select(value => new StrategyPriceBar(value.OpenTimeUtc,
                value.Open, value.High, value.Low, value.Close)).ToArray(),
            signal.Direction, shadowStructure, multiTimeframeV2,
            marketOptions.Value.CandleIntervalSeconds,
            strategyContext.CurrentPrice * strategyContext.AtrPercent / 100m,
            strategyContext.OpeningRangeHigh, strategyContext.OpeningRangeLow);
        if (!preQuoteGate.Permitted)
        {
            await PersistStrategyEvaluationAsync(db, strategy, instrument.Id, candleDecisionTime,
                latestCandle.Close, openingRangeHigh, openingRangeLow, vwap, fast, slow, atr,
                relativeVolume, regime, preQuoteGate.RejectionOutcome,
                preQuoteGate.Reasons,
                signal, null, null, cancellationToken, shadowStructure);
            state.Record(preQuoteGate.RejectionOutcome, true,
                preQuoteGate.RejectionOutcome == "MarketLocationRejected"
                    ? $"{preQuoteGate.LocationContext}. {string.Join(" ", preQuoteGate.Reasons)}"
                    : string.Join(" ", preQuoteGate.Reasons),
                tradeState.TradesToday, tradeState.RealisedPnl,
                signalId: signal.SignalId, direction: signal.Direction.ToString());
            return;
        }

        var underlyingRiskDistance = Math.Abs(signal.ProposedEntry - signal.ProposedStopLoss);
        if (underlyingRiskDistance <= 0m)
            underlyingRiskDistance = strategyContext.CurrentPrice * strategyContext.AtrPercent / 100m;
        var optionInterest = await OptionOpenInterestEntryContext.EvaluateAsync(db, instrument.Id,
            signal.Direction, strategyContext.CurrentPrice, underlyingRiskDistance, now,
            cancellationToken);
        var optionInterestOutcome = !optionInterest.Available
            ? "OptionOpenInterestUnavailable"
            : optionInterest.Permitted
                ? "OptionOpenInterestAccepted"
                : "OptionOpenInterestBarrierRejected";
        await PersistStrategyEvaluationAsync(db, strategy, instrument.Id, candleDecisionTime,
            latestCandle.Close, openingRangeHigh, openingRangeLow, vwap, fast, slow, atr,
            relativeVolume, regime, optionInterestOutcome, optionInterest.Reasons,
            signal, null, null, cancellationToken, shadowStructure);
        if (!optionInterest.Permitted)
        {
            state.Record("OptionOpenInterestBarrierRejected", true,
                string.Join(" ", optionInterest.Reasons), tradeState.TradesToday,
                tradeState.RealisedPnl, signalId: signal.SignalId,
                direction: signal.Direction.ToString());
            return;
        }

        var strongRegimeAligned = regime.DirectionalBias == signal.Direction &&
            regime.Regime is MarketRegime.GapDownContinuation or MarketRegime.GapUpContinuation or
                MarketRegime.StrongBearishTrend or MarketRegime.StrongBullishTrend;
        var entryQuality = PaperEntryQualityPolicy.Evaluate(shadowStructure, signal.Direction,
            signal.StrategyId, strongRegimeAligned, regime.Confidence, relativeVolume);
        if (!entryQuality.Permitted)
        {
            await PersistStrategyEvaluationAsync(db, strategy, instrument.Id, candleDecisionTime,
                latestCandle.Close, openingRangeHigh, openingRangeLow, vwap, fast, slow, atr,
                relativeVolume, regime, "EntryQualityRejected", entryQuality.Reasons,
                signal, null, null, cancellationToken, shadowStructure);
            state.Record("EntryQualityRejected", true, string.Join(" ", entryQuality.Reasons),
                tradeState.TradesToday, tradeState.RealisedPnl, signalId: signal.SignalId,
                direction: signal.Direction.ToString());
            return;
        }

        var adaptiveDecision = AdaptivePaperTradePolicy.Evaluate(tradeState.TradesToday,
            int.MaxValue, tradeState.ConsecutiveLosses,
            int.MaxValue, tradeState.LastLossAtUtc, now,
            0, false,
            0);
        if (!adaptiveDecision.Permitted)
        {
            await PersistStrategyEvaluationAsync(db, strategy, instrument.Id, candleDecisionTime,
                latestCandle.Close, openingRangeHigh, openingRangeLow, vwap, fast, slow, atr,
                relativeVolume, regime, "AdaptiveRiskRejected", adaptiveDecision.Reasons,
                signal, null, null, cancellationToken, shadowStructure);
            state.Record("AdaptiveRiskRejected", true, string.Join(" ", adaptiveDecision.Reasons),
                tradeState.TradesToday, tradeState.RealisedPnl, signalId: signal.SignalId,
                direction: signal.Direction.ToString());
            return;
        }

        var hasOpposingExposure = tradeState.OpenPositions.Any(open =>
            open.Signal.Direction != signal.Direction);
        if (hasOpposingExposure)
        {
            if (!options.Value.SelectiveHedgingEnabled)
            {
                state.Record("HedgeRejected", true,
                    "An opposite setup was detected, but selective paper hedging is disabled.",
                    tradeState.TradesToday, tradeState.RealisedPnl);
                return;
            }
            var hedgeDecision = SelectiveHedgePolicy.Evaluate(new SelectiveHedgeInput(
                    true,
                    tradeState.OpenPositions.Any(open =>
                        open.Signal.StrategyId.StartsWith("selective-hedge-", StringComparison.Ordinal)),
                    atr, relativeVolume, signal.Confidence, regime.Regime),
                options.Value.HedgeMinimumAtrPercent,
                options.Value.HedgeMinimumRelativeFuturesVolume,
                options.Value.HedgeMinimumSignalConfidence);
            if (!hedgeDecision.Approved)
            {
                await PersistStrategyEvaluationAsync(db, strategy, instrument.Id, candleDecisionTime,
                    latestCandle.Close, openingRangeHigh, openingRangeLow, vwap, fast, slow, atr,
                    relativeVolume, regime, "HedgeRejected", hedgeDecision.Reasons,
                    signal, null, null, cancellationToken, shadowStructure);
                state.Record("HedgeRejected", true, string.Join(" ", hedgeDecision.Reasons),
                    tradeState.TradesToday, tradeState.RealisedPnl);
                return;
            }
            signal = signal with
            {
                StrategyId = $"selective-hedge-{signal.StrategyId}",
                SupportingReasons = signal.SupportingReasons
                    .Append("Opposite long-option hedge qualified by volatility policy.").ToArray()
            };
        }

        var audit = scope.ServiceProvider.GetRequiredService<IPaperLifecycleAuditStore>();
        await audit.PersistSignalAsync(signal, cancellationToken);
        var candidates = await db.Instruments.AsNoTracking().Where(value =>
                value.Exchange == "NSE" && value.Segment == InstrumentSegment.FuturesAndOptions &&
                (value.Type == InstrumentType.CallOption || value.Type == InstrumentType.PutOption) &&
                value.TradingSymbol.StartsWith("NIFTY") &&
                value.IsActive && value.ExpiryDate != null && value.StrikePrice != null)
            .Select(value => new NiftyOptionContractCandidate(value.Id, value.TradingSymbol, value.Type,
                value.ExpiryDate!.Value, value.StrikePrice!.Value, value.LotSize, value.TickSize))
            .ToListAsync(cancellationToken);
        var selectedOption = NiftyOptionContractSelector.Select(
            candidates, signal.Direction, currentPrice, DateOnly.FromDateTime(indiaNow.Date),
            new NiftyOptionSelectionOptions(options.Value.MaximumOptionDaysToExpiry,
                options.Value.ExpiryDayInTheMoneySteps, options.Value.NormalInTheMoneySteps,
                options.Value.OptionStrikeStep) with
            {
                NormalInTheMoneySteps = ProfessionalTradeFramework.Map(marketStateV2) ==
                    ProfessionalTradeRegime.Directional
                    ? Math.Max(1, options.Value.NormalInTheMoneySteps)
                    : options.Value.NormalInTheMoneySteps
            });
        if (selectedOption is null)
        {
            await PersistStrategyEvaluationAsync(db, strategy, instrument.Id, candleDecisionTime,
                latestCandle.Close, openingRangeHigh, openingRangeLow, vwap, fast, slow, atr,
                relativeVolume, regime, "OptionUnavailable",
                ["No valid Nifty option contract was available."], signal, null, null,
                cancellationToken, shadowStructure);
            state.Record("OptionUniverseUnavailable", false,
                "A Nifty signal qualified, but no valid Nifty option contract was available. Index execution is blocked.",
                tradeState.TradesToday, tradeState.RealisedPnl, signalId: signal.SignalId,
                direction: signal.Direction.ToString());
            return;
        }

        var duplicateActiveContract = ActiveContractEntryPolicy.IsDuplicate(
            tradeState.OpenPositions.Select(open => new ActiveContractExposure(
                open.Entry.InstrumentId, open.Entry.Direction)),
            selectedOption.InstrumentId,
            Direction.Buy);
        if (duplicateActiveContract)
        {
            var reason = $"{selectedOption.TradingSymbol} already has an active long paper position; " +
                         "the existing position will continue to be managed instead of adding duplicate exposure.";
            await PersistStrategyEvaluationAsync(db, strategy, instrument.Id, candleDecisionTime,
                latestCandle.Close, openingRangeHigh, openingRangeLow, vwap, fast, slow, atr,
                relativeVolume, regime, "DuplicateActiveContract", [reason],
                signal, selectedOption, null, cancellationToken, shadowStructure);
            state.Record("DuplicateActiveContract", true, reason,
                tradeState.TradesToday, tradeState.RealisedPnl, signalId: signal.SignalId,
                direction: signal.Direction.ToString(), optionSymbol: selectedOption.TradingSymbol,
                optionType: selectedOption.Type.ToString(), optionExpiry: selectedOption.ExpiryDate,
                optionStrike: selectedOption.StrikePrice, optionLotSize: selectedOption.LotSize);
            return;
        }

        var growwGateway = scope.ServiceProvider.GetRequiredService<IGrowwReadOnlyGateway>();
        var quote = await growwGateway.GetQuoteAsync(new GrowwQuoteRequest(
            "NSE", "FNO", selectedOption.TradingSymbol), cancellationToken);
        var tradingDate = DateOnly.FromDateTime(indiaNow.Date);
        var hasCorrelatedSensexExposure = await (from openPosition in db.MarketPaperPositions.AsNoTracking()
            join openInstrument in db.Instruments.AsNoTracking()
                on openPosition.ExecutionInstrumentId equals openInstrument.Id
            where openPosition.Market == "sensex" && openPosition.Status == "Active" &&
                  (signal.Direction == Direction.Buy
                      ? openInstrument.Type == InstrumentType.CallOption
                      : openInstrument.Type == InstrumentType.PutOption)
            select openPosition.Id).AnyAsync(cancellationToken);
        var riskOptions = scope.ServiceProvider.GetRequiredService<IOptions<PreliminaryRiskOptions>>().Value;
        var existingCapitalExposure = tradeState.OpenPositions.Sum(open =>
            open.Entry.AverageFillPrice.GetValueOrDefault() * open.RemainingQuantity);
        var openUnrealisedPnl = state.GetCurrent().ActivePositionMarks?.Sum(mark =>
            mark.UnrealisedPnl.GetValueOrDefault()) ?? 0m;
        var admission = NiftyOptionAdmissionPolicy.Evaluate(new(
            quote, options.Value.PermissivePaperExecution,
            options.Value.MaximumOptionSpreadPercent,
            options.Value.MaximumOptionPremium,
            options.Value.MinimumOptionVolume,
            options.Value.MinimumOptionOpenInterest,
            tradingDate, selectedOption.ExpiryDate,
            options.Value.OptionStopLossPercent,
            options.Value.OptionRewardToRiskRatio, selectedOption.TickSize,
            selectedOption.LotSize, configuredLots,
            new ProfessionalTradeAssessmentInput("nifty",
                ProfessionalTradeFramework.Map(marketStateV2), signal.Confidence,
                relativeVolume, shadowStructure.RoomToRiskRatio,
                null, null, null, 0, null, null, 0m,
                hasCorrelatedSensexExposure, tradeState.ConsecutiveLosses,
                reentryDecision.NewStructureObserved),
            new PaperPortfolioRiskInput(0m, 0m, selectedOption.LotSize,
                configuredLots, riskOptions.MaximumRiskPerTrade,
                riskOptions.MaximumCapitalExposure, existingCapitalExposure,
                tradeState.RealisedPnl, openUnrealisedPnl,
                options.Value.MaximumDailyLoss, tradeState.OpenPositions.Count,
                options.Value.MaximumConcurrentPositions, killSwitchActive,
                true, fresh, dailyLossLimitOverridden)));
        var pricing = admission.Pricing;
        if (!pricing.Approved)
        {
            var rejected = new RiskDecisionResult(false, 0, 0m, 0m,
                pricing.RejectionReasons, 0m, 0m);
            var rejectedProposal = new PaperOptionExecutionProposal(
                selectedOption.InstrumentId, selectedOption.TradingSymbol,
                selectedOption.Type.ToString(), selectedOption.ExpiryDate,
                selectedOption.StrikePrice, selectedOption.LotSize,
                configuredLots, quote.OfferPrice ?? quote.LastPrice,
                0m, 0m);
            await audit.PersistRiskDecisionAsync(signal.SignalId, rejected, cancellationToken,
                rejectedProposal);
            await PersistStrategyEvaluationAsync(db, strategy, instrument.Id, candleDecisionTime,
                latestCandle.Close, openingRangeHigh, openingRangeLow, vwap, fast, slow, atr,
                relativeVolume, regime, "OptionQuoteRejected", pricing.RejectionReasons,
                signal, selectedOption, quote.OfferPrice ?? quote.LastPrice, cancellationToken,
                shadowStructure);
            state.Record("LiquidityRejected", false,
                $"{selectedOption.TradingSymbol} was rejected: {string.Join(" ", pricing.RejectionReasons)}",
                tradeState.TradesToday, tradeState.RealisedPnl, signalId: signal.SignalId,
                direction: signal.Direction.ToString(), optionSymbol: selectedOption.TradingSymbol,
                optionType: selectedOption.Type.ToString(), optionExpiry: selectedOption.ExpiryDate,
                optionStrike: selectedOption.StrikePrice, optionLotSize: selectedOption.LotSize);
            return;
        }

        var maximumLots = configuredLots;
        var protective = (StopLoss: admission.StopLoss, Target: admission.Target);
        var professionalPlan = admission.ProfessionalPlan!;
        if (!professionalPlan.Permitted)
        {
            await PersistStrategyEvaluationAsync(db, strategy, instrument.Id, candleDecisionTime,
                latestCandle.Close, openingRangeHigh, openingRangeLow, vwap, fast, slow, atr,
                relativeVolume, regime, "ProfessionalPlanRejected", professionalPlan.Reasons,
                signal, selectedOption, pricing.EntryPrice, cancellationToken, shadowStructure);
            state.Record("ProfessionalPlanRejected", true,
                string.Join(" ", professionalPlan.Reasons), tradeState.TradesToday,
                tradeState.RealisedPnl, signalId: signal.SignalId,
                direction: signal.Direction.ToString(), optionSymbol: selectedOption.TradingSymbol);
            return;
        }
        var executionSignal = signal with
        {
            InstrumentId = selectedOption.InstrumentId,
            Direction = Direction.Buy,
            ProposedEntry = pricing.EntryPrice,
            ProposedStopLoss = protective.StopLoss,
            ProposedTarget = protective.Target,
            RewardToRiskRatio = options.Value.OptionRewardToRiskRatio
        };
        var optionProposal = new PaperOptionExecutionProposal(
            selectedOption.InstrumentId, selectedOption.TradingSymbol,
            selectedOption.Type.ToString(), selectedOption.ExpiryDate,
            selectedOption.StrikePrice, selectedOption.LotSize,
            maximumLots, pricing.EntryPrice,
            protective.StopLoss, protective.Target);
        var decision = admission.RiskDecision!;
        // Shared admission returns whole lots within the cost-inclusive risk budget.
        // Never restore the configured ceiling after it has reduced the approved size.
        await audit.PersistRiskDecisionAsync(signal.SignalId, decision, cancellationToken,
            optionProposal);
        if (!decision.Approved)
        {
            await PersistStrategyEvaluationAsync(db, strategy, instrument.Id, candleDecisionTime,
                latestCandle.Close, openingRangeHigh, openingRangeLow, vwap, fast, slow, atr,
                relativeVolume, regime, "RiskRejected", decision.RejectionReasons,
                signal, selectedOption, pricing.EntryPrice, cancellationToken, shadowStructure);
            state.Record("RiskRejected", false, string.Join(" ", decision.RejectionReasons),
                tradeState.TradesToday, tradeState.RealisedPnl, signalId: signal.SignalId,
                direction: signal.Direction.ToString(), optionSymbol: selectedOption.TradingSymbol,
                optionType: selectedOption.Type.ToString(), optionExpiry: selectedOption.ExpiryDate,
                optionStrike: selectedOption.StrikePrice, optionLotSize: selectedOption.LotSize);
            return;
        }

        var entryReference = $"{signal.SignalId:N}-ENTRY";
        await broker.SubmitAsync(new BrokerOrderRequest(entryReference,
            selectedOption.InstrumentId, Direction.Buy, decision.ApprovedQuantity,
            pricing.EntryPrice), cancellationToken);
        var entry = await paperControl.ProcessNextFillAsync(entryReference, cancellationToken);
        if (entry.State != OrderState.Filled)
            throw new InvalidOperationException($"Paper option entry ended in state {entry.State}.");
        scope.ServiceProvider.GetRequiredService<LiveExecutionWakeSignal>().Notify();

        await TracePaperProtectionAsync(db, signal.SignalId, instrument.Id, decision.FinalStopLoss,
            entry.FilledQuantity, entry.UpdatedAtUtc, cancellationToken);
        await PersistStrategyEvaluationAsync(db, strategy, instrument.Id, candleDecisionTime,
            latestCandle.Close, openingRangeHigh, openingRangeLow, vwap, fast, slow, atr,
            relativeVolume, regime, "PaperPositionOpened", [], signal, selectedOption,
            entry.AverageFillPrice, cancellationToken, shadowStructure);
        await PersistTradePriceSampleAsync(db, signal.SignalId, selectedOption.InstrumentId,
            entry.AverageFillPrice!.Value, cancellationToken);

        // Supplement the existing signal/evaluation records, never substitute Sensex rules.
        var reasoningCharges = PaperTradingCostModel.CalculateOptionCharges([
            new(PaperOptionTransactionSide.Buy, entry.AverageFillPrice.Value, entry.FilledQuantity),
            new(PaperOptionTransactionSide.Sell, decision.FinalTarget, entry.FilledQuantity)]);
        await scope.ServiceProvider.GetRequiredService<IAuditWriter>().WriteAsync(new AuditEntry(
            "paper-automation", "NiftyTradeReasoning", "Signal", signal.SignalId.ToString("N"),
            "Versioned observation-only entry evidence; see linked signal/evaluation and exit records.",
            "{}", JsonSerializer.Serialize(new
            {
                version = "nifty-reasoning-v1", market = "nifty", advisoryOnly = true,
                signalId = signal.SignalId, signal.StrategyId, signal.StrategyVersion,
                underlyingDirection = signal.Direction.ToString(),
                supportingReasons = signal.SupportingReasons,
                invalidatingReasons = signal.InvalidatingReasons,
                context = new { regime, shadowStructure, vwap, fast, slow, atrPercent = atr,
                    relativeFuturesVolume = relativeVolume, openingRangeHigh, openingRangeLow },
                location = preQuoteGate.Location, entryQuality,
                timing = new { signal.MarketDataTimestampUtc, signal.ExpiresAtUtc,
                    candleDecisionTime, recordedAtUtc = timeProvider.GetUtcNow(),
                    completedBarsSincePriorSameDirectionExit = reentryDecision.CompletedBarsSinceExit,
                    structuralResetObserved = reentryDecision.NewStructureObserved,
                    reentryEvidence = reentryDecision.Reasons,
                    limitation = "Candle-derived setup timing does not measure broker-network latency." },
                quoteSnapshot = quote,
                pricingMode = options.Value.PermissivePaperExecution ? "PermissivePaperSimulation" : "ValidatedPaperQuote",
                option = optionProposal,
                filledEntry = entry.AverageFillPrice.Value, quantity = entry.FilledQuantity,
                initialStop = decision.FinalStopLoss, target = decision.FinalTarget,
                initialPremiumRisk = (entry.AverageFillPrice.Value - decision.FinalStopLoss) * entry.FilledQuantity,
                plannedNetTargetReward = (decision.FinalTarget - entry.AverageFillPrice.Value) *
                    entry.FilledQuantity - reasoningCharges.Total,
                estimatedTargetRoundTripCharges = reasoningCharges,
                exposure = new { existingCapitalExposure, openPositionsBeforeEntry = tradeState.OpenPositions.Count,
                    openUnrealisedPnl },
                adaptiveDecision, professionalPlan,
                limitations = "Target reward is a plan, not a forecast; unknown slippage is excluded. " +
                    "Unknown setup age is not inferred. No Sensex thresholds or verdicts are applied. " +
                    "Entry reasons must be compared with later net outcomes."
            }), signal.SignalId.ToString("N"), timeProvider.GetUtcNow()), cancellationToken);

        state.Record("PositionOpen", false,
            $"Paper position opened in {selectedOption.TradingSymbol}; monitoring option premium SL and target.",
            tradeState.TradesToday + 1, tradeState.RealisedPnl, signalId: signal.SignalId,
            direction: Direction.Buy.ToString(), quantity: entry.FilledQuantity,
            entry: entry.AverageFillPrice, stop: decision.FinalStopLoss, target: decision.FinalTarget,
            optionSymbol: selectedOption.TradingSymbol,
            optionType: selectedOption.Type.ToString(), optionExpiry: selectedOption.ExpiryDate,
            optionStrike: selectedOption.StrikePrice, optionLotSize: selectedOption.LotSize,
            currentOptionPrice: entry.AverageFillPrice);
    }

    private async Task<bool> ManageOpenPositionAsync(IServiceProvider services, RebuiltTradeState tradeState,
        OpenTrade open, decimal price, TimeSpan indiaTime, bool fresh, bool underlyingFresh, bool killSwitchActive,
        Instrument optionInstrument, Guid underlyingInstrumentId, DateTimeOffset sessionStart,
        DateTimeOffset sessionEnd, CancellationToken cancellationToken)
    {
        if (!fresh || price <= 0)
        {
            state.Record("PositionUnmonitored", false,
                "The option bid is unavailable; the paper position remains open and new entries are blocked.",
                tradeState.TradesToday, tradeState.RealisedPnl, 0m, open.Signal.SignalId,
                open.Entry.Direction.ToString(), open.Entry.FilledQuantity,
                open.Entry.AverageFillPrice, open.StopLoss, open.Target,
                optionSymbol: optionInstrument.TradingSymbol,
                optionType: optionInstrument.Type.ToString(), optionExpiry: optionInstrument.ExpiryDate,
                optionStrike: optionInstrument.StrikePrice, optionLotSize: optionInstrument.LotSize,
                currentOptionPrice: price > 0 ? price : null);
            return true;
        }

        var researchDb = services.GetRequiredService<TradingDbContext>();
        var manualExitRequested = await ManualTradeExitRequest.ExistsAsync(researchDb, "nifty",
            open.Signal.SignalId, cancellationToken);
        await PersistTradePriceSampleAsync(researchDb, open.Signal.SignalId,
            optionInstrument.Id, price, cancellationToken);

        var multiplier = open.Entry.Direction == Direction.Buy ? 1m : -1m;
        var entryPrice = open.AverageEntryPrice;
        var exitPlan = ProfessionalExitPolicy.For(ProfessionalExitPolicy.From(open.Signal.Regime));
        var persistedPrices = researchDb.PaperTradePriceSamples.AsNoTracking()
            .Where(value => value.SignalId == open.Signal.SignalId);
        var peakPoint = open.Entry.Direction == Direction.Buy
            ? await persistedPrices.OrderByDescending(value => value.OptionPrice)
                .Select(value => new { value.OptionPrice, value.ObservedAtUtc }).FirstOrDefaultAsync(cancellationToken)
            : await persistedPrices.OrderBy(value => value.OptionPrice)
                .Select(value => new { value.OptionPrice, value.ObservedAtUtc }).FirstOrDefaultAsync(cancellationToken);
        var worstPoint = open.Entry.Direction == Direction.Buy
            ? await persistedPrices.OrderBy(value => value.OptionPrice)
                .Select(value => new { value.OptionPrice, value.ObservedAtUtc }).FirstOrDefaultAsync(cancellationToken)
            : await persistedPrices.OrderByDescending(value => value.OptionPrice)
                .Select(value => new { value.OptionPrice, value.ObservedAtUtc }).FirstOrDefaultAsync(cancellationToken);
        var persistedFavourable = peakPoint?.OptionPrice ?? entryPrice;
        var favourable = favourablePriceBySignal.GetValueOrDefault(open.Signal.SignalId,
            persistedFavourable);
        favourable = open.Entry.Direction == Direction.Buy
            ? Math.Max(favourable, persistedFavourable)
            : Math.Min(favourable, persistedFavourable);
        favourable = open.Entry.Direction == Direction.Buy ? Math.Max(favourable, price) : Math.Min(favourable, price);
        favourablePriceBySignal[open.Signal.SignalId] = favourable;
        var effectiveStop = PaperProtectiveStopPolicy.CalculateNetProtected(
            open.Entry.Direction, entryPrice, open.StopLoss, favourable,
            exitPlan, options.Value.BreakEvenEnabled,
            options.Value.TrailingStopEnabled, open.TotalQuantity,
            open.Entry.FilledQuantity, "NSE");
        var initialRiskPerUnit = Math.Abs(entryPrice - open.StopLoss);
        var favourableMove = (favourable - entryPrice) * multiplier;
        var unrealised = (price - entryPrice) * open.RemainingQuantity * multiplier;
        await PersistProfitProtectionSnapshotAsync(researchDb, open, underlyingInstrumentId,
            price, effectiveStop, peakPoint?.OptionPrice ?? price,
            peakPoint?.ObservedAtUtc ?? timeProvider.GetUtcNow(),
            worstPoint?.OptionPrice ?? price,
            worstPoint?.ObservedAtUtc ?? timeProvider.GetUtcNow(), cancellationToken);

        var invalidation = options.Value.UnderlyingTrendInvalidationEnabled && underlyingFresh
            ? await EvaluateUnderlyingInvalidationAsync(services, open, underlyingInstrumentId,
                sessionStart, sessionEnd, cancellationToken)
            : new UnderlyingTrendInvalidationResult(false, 0m,
                underlyingFresh ? ["Underlying trend invalidation is disabled."] :
                    ["Fresh Nifty data is unavailable; reversal exits fail closed."]);

        var reversalProtected = invalidation.ShouldExit && unrealised > 0 &&
                                options.Value.ProtectProfitableConfirmedReversals;
        if (reversalProtected)
        {
            effectiveStop = PaperProtectiveStopPolicy.ProtectReversalProfit(open.Entry.Direction,
                entryPrice, price, effectiveStop, options.Value.ReversalProfitLockFraction);
            invalidation = invalidation with
            {
                ShouldExit = false,
                SupportingEvidence = invalidation.SupportingEvidence
                    .Append($"Profitable reversal protected at {options.Value.ReversalProfitLockFraction:P0} of current premium gain.")
                    .ToArray()
            };
        }

        effectiveStop = await PersistLiveProtectionDesiredAsync(researchDb, open.Signal.SignalId,
            underlyingInstrumentId, open.Entry.Direction, open.StopLoss, optionInstrument.TickSize, effectiveStop, open.RemainingQuantity, cancellationToken);

        var extendBeyondTarget = options.Value.TargetExtensionEnabled && underlyingFresh &&
                                 exitPlan.ExtendTarget &&
                                 !invalidation.ShouldExit &&
                                 invalidation.DirectionAligned &&
                                 invalidation.Behaviour == SessionBehaviour.Trending &&
                                 open.Signal.Confidence >= options.Value.TargetExtensionMinimumConfidence;
        var targetReachedForAddOn = open.Entry.Direction == Direction.Buy
            ? price >= open.Target : price <= open.Target;
        var premiumStopBreached = open.Entry.Direction == Direction.Buy
            ? price <= effectiveStop : price >= effectiveStop;
        if (!premiumStopBreached && extendBeyondTarget && targetReachedForAddOn && open.AddOn is null &&
            open.PartialExit is null && open.Signal.Confidence >= .72m)
        {
            var addOnRiskPerUnit = Math.Max(0m, (price - effectiveStop) * multiplier);
            var addOnDecision = WinnerAddOnPolicy.Evaluate(
                initialRiskPerUnit <= 0m ? 0m : favourableMove / initialRiskPerUnit,
                true, false, initialRiskPerUnit * open.Entry.FilledQuantity,
                addOnRiskPerUnit * optionInstrument.LotSize);
            if (addOnDecision.Permitted && !manualExitRequested)
            {
                var addOnReference = $"{open.Signal.SignalId:N}-ADDON";
                await broker.SubmitAsync(new(addOnReference, open.Entry.InstrumentId,
                    open.Entry.Direction, optionInstrument.LotSize, price), cancellationToken);
                var addOn = await paperControl.ProcessNextFillAsync(addOnReference, cancellationToken);
                if (addOn.FilledQuantity > 0)
                    await TracePaperProtectionAsync(researchDb, open.Signal.SignalId, underlyingInstrumentId,
                        effectiveStop, open.RemainingQuantity + addOn.FilledQuantity, addOn.UpdatedAtUtc, cancellationToken);
                await services.GetRequiredService<IAuditWriter>().WriteAsync(new AuditEntry(
                    "paper-automation", "WinnerAddOn", "Signal", open.Signal.SignalId.ToString("N"),
                    "One winner add-on was filled after 1R and confirmed continuation; live mirroring depends on broker execution.",
                    "{}", JsonSerializer.Serialize(new { price, effectiveStop,
                        quantity = optionInstrument.LotSize, addOnDecision.Reasons,
                        liveMirrorCandidate = effectiveStop > .05m }), open.Signal.SignalId.ToString("N"),
                    timeProvider.GetUtcNow()), cancellationToken);
                state.Record("PositionOpen", false,
                    $"Directional winner add-on filled: {addOn.FilledQuantity} units; no losing position was averaged down.",
                    tradeState.TradesToday, tradeState.RealisedPnl, unrealised,
                    open.Signal.SignalId, open.Entry.Direction.ToString(),
                    open.RemainingQuantity + addOn.FilledQuantity, entryPrice,
                    effectiveStop, open.Target, optionSymbol: optionInstrument.TradingSymbol,
                    currentOptionPrice: price);
                return false;
            }
        }
        var exitReason = NiftyOpenPositionDecisionPolicy.ResolveExit(
            open.Entry.Direction, price, effectiveStop, open.Target,
            TimeOnly.FromTimeSpan(indiaTime), ParseTime(options.Value.ForcedExit),
            fresh, extendBeyondTarget, manualExitRequested, killSwitchActive,
            invalidation.ShouldExit, false, false);
        var confirmedFailedSetup = false;
        if (exitReason == PaperExitReason.None && fresh && underlyingFresh &&
            FailedSetupExitPolicy.IsAdverseCandidate(entryPrice, price,
                initialRiskPerUnit, favourable, open.Entry.UpdatedAtUtc,
                timeProvider.GetUtcNow()))
        {
            var latestUnderlying = await researchDb.MarketObservations.AsNoTracking()
                .Where(value => value.InstrumentId == underlyingInstrumentId && value.Source == "Groww")
                .OrderByDescending(value => value.ReceivedAtUtc).FirstOrDefaultAsync(cancellationToken);
            var intervalSeconds = marketOptions.Value.CandleIntervalSeconds;
            var underlyingBars = await researchDb.Candles.AsNoTracking()
                .Where(value => value.InstrumentId == underlyingInstrumentId && value.Source == "Groww" &&
                                value.IntervalSeconds == intervalSeconds && value.OpenTimeUtc >= sessionStart &&
                                value.OpenTimeUtc < sessionEnd)
                .OrderByDescending(value => value.OpenTimeUtc).Take(30)
                .OrderBy(value => value.OpenTimeUtc).ToListAsync(cancellationToken);
            var observedAt = timeProvider.GetUtcNow();
            if (latestUnderlying is not null && latestUnderlying.ReceivedAtUtc <= observedAt &&
                observedAt - latestUnderlying.ReceivedAtUtc <=
                TimeSpan.FromSeconds(marketOptions.Value.MaximumAgeSeconds))
            {
                var completedBars = underlyingBars
                    .Where(value => value.OpenTimeUtc.AddSeconds(value.IntervalSeconds) <= observedAt)
                    .Select(value => new StrategyPriceBar(value.OpenTimeUtc,
                        value.Open, value.High, value.Low, value.Close)).ToArray();
                var failedSetup = FailedSetupExitPolicy.EvaluateForMarket("nifty", open.Signal.StrategyId,
                    open.Signal.Direction,
                    entryPrice, price, initialRiskPerUnit, favourable,
                    open.Entry.UpdatedAtUtc, observedAt,
                    latestUnderlying.Price, latestUnderlying.ReceivedAtUtc, completedBars,
                    intervalSeconds, marketOptions.Value.MaximumAgeSeconds);
                if (failedSetup.ShouldExit)
                {
                    confirmedFailedSetup = true;
                    invalidation = invalidation with
                    {
                        SupportingEvidence = [.. invalidation.SupportingEvidence,
                            .. failedSetup.Reasons]
                    };
                }
            }
        }
        if (confirmedFailedSetup)
            exitReason = NiftyOpenPositionDecisionPolicy.ResolveExit(
                open.Entry.Direction, price, effectiveStop, open.Target,
                TimeOnly.FromTimeSpan(indiaTime), ParseTime(options.Value.ForcedExit),
                fresh, extendBeyondTarget, manualExitRequested, killSwitchActive,
                invalidation.ShouldExit, true, false);
        if (exitReason == PaperExitReason.ForcedIntradayExit && options.Value.WeeklyCarryForwardEnabled)
        {
            var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(
                timeProvider.GetUtcNow(), IndiaTimeZone).Date);
            var carry = WeeklyCarryPolicy.Evaluate(new WeeklyCarryInput(localDate,
                    optionInstrument.ExpiryDate, open.Entry.Direction, entryPrice, price,
                    open.Signal.Confidence, invalidation.ShouldExit, killSwitchActive),
                options.Value.WeeklyCarryMaximumDaysToExpiry,
                options.Value.WeeklyCarryMinimumConfidence);
            if (carry.Approved)
                exitReason = NiftyOpenPositionDecisionPolicy.ResolveExit(
                    open.Entry.Direction, price, effectiveStop, open.Target,
                    TimeOnly.FromTimeSpan(indiaTime), ParseTime(options.Value.ForcedExit),
                    fresh, extendBeyondTarget, manualExitRequested,
                    killSwitchActive, invalidation.ShouldExit,
                    confirmedFailedSetup, true);
        }

        var partialQuantity = NiftyOpenPositionDecisionPolicy.PartialQuantity(
            open.Entry.Direction, price, entryPrice, open.StopLoss,
            open.TotalQuantity, optionInstrument.LotSize, exitPlan, options.Value.PartialProfitEnabled,
            open.PartialExit is not null, exitReason);
        if (partialQuantity > 0)
        {
            var partialReference = $"{open.Signal.SignalId:N}-PARTIAL";
            var partialDirection = open.Entry.Direction == Direction.Buy ? Direction.Sell : Direction.Buy;
            await broker.SubmitAsync(new(partialReference, open.Entry.InstrumentId, partialDirection,
                partialQuantity, price), cancellationToken);
            var partialFill = await paperControl.ProcessNextFillAsync(partialReference, cancellationToken);
            if (partialFill.FilledQuantity > 0)
                await TracePaperProtectionAsync(researchDb, open.Signal.SignalId, underlyingInstrumentId,
                    effectiveStop, open.RemainingQuantity - partialFill.FilledQuantity, partialFill.UpdatedAtUtc, cancellationToken);
            state.Record("PositionOpen", false,
                $"Partial profit booked on {partialQuantity} units; break-even/trailing protection is active.",
                tradeState.TradesToday, tradeState.RealisedPnl, unrealised, open.Signal.SignalId,
                open.Entry.Direction.ToString(), open.RemainingQuantity - partialQuantity, entryPrice,
                effectiveStop, open.Target, optionSymbol: optionInstrument.TradingSymbol,
                optionType: optionInstrument.Type.ToString(), optionExpiry: optionInstrument.ExpiryDate,
                optionStrike: optionInstrument.StrikePrice, optionLotSize: optionInstrument.LotSize,
                currentOptionPrice: price);
            return false;
        }
        if (exitReason == PaperExitReason.None)
        {
            state.Record(fresh ? "PositionOpen" : "PositionUnmonitored", false,
                fresh ? reversalProtected
                    ? "Confirmed reversal detected; premium profit is protected by a tightened stop."
                    : extendBeyondTarget &&
                      (open.Entry.Direction == Direction.Buy ? price >= open.Target : price <= open.Target)
                        ? "Original target reached; fresh direction-aligned structure remains valid, so runner mode is trailing profit."
                        : "Monitoring premium protection and confirmed Nifty trend invalidation." :
                    "Market data is stale; new entries are blocked and the paper position cannot be repriced.",
                tradeState.TradesToday, tradeState.RealisedPnl, unrealised, open.Signal.SignalId,
                open.Entry.Direction.ToString(), open.RemainingQuantity, entryPrice,
                effectiveStop, open.Target, optionSymbol: optionInstrument.TradingSymbol,
                optionType: optionInstrument.Type.ToString(), optionExpiry: optionInstrument.ExpiryDate,
                optionStrike: optionInstrument.StrikePrice, optionLotSize: optionInstrument.LotSize,
                currentOptionPrice: price);
            return false;
        }

        var exitReference = $"{open.Signal.SignalId:N}-EXIT";
        var exitDirection = open.Entry.Direction == Direction.Buy ? Direction.Sell : Direction.Buy;
        await broker.SubmitAsync(new(exitReference, open.Entry.InstrumentId, exitDirection,
            open.RemainingQuantity, price), cancellationToken);
        var exit = await paperControl.ProcessNextFillAsync(exitReference, cancellationToken);
        var grossPnl = (exit.AverageFillPrice!.Value - entryPrice) *
                       open.RemainingQuantity * multiplier;
        if (open.PartialExit?.AverageFillPrice is { } partialPrice)
            grossPnl += (partialPrice - entryPrice) *
                        open.PartialExit.FilledQuantity * multiplier;
        var transactions = new List<PaperOptionTransaction>
        {
            new(open.Entry.Direction == Direction.Buy ? PaperOptionTransactionSide.Buy :
                PaperOptionTransactionSide.Sell, open.Entry.AverageFillPrice.GetValueOrDefault(),
                open.Entry.FilledQuantity)
        };
        if (open.AddOn?.AverageFillPrice is { } addOnEntryPrice)
            transactions.Add(new(open.AddOn.Direction == Direction.Buy ? PaperOptionTransactionSide.Buy :
                PaperOptionTransactionSide.Sell, addOnEntryPrice, open.AddOn.FilledQuantity));
        if (open.PartialExit?.AverageFillPrice is { } bookedPartialPrice)
            transactions.Add(new(open.Entry.Direction == Direction.Buy ? PaperOptionTransactionSide.Sell :
                PaperOptionTransactionSide.Buy, bookedPartialPrice, open.PartialExit.FilledQuantity));
        transactions.Add(new(exitDirection == Direction.Buy ? PaperOptionTransactionSide.Buy :
            PaperOptionTransactionSide.Sell, exit.AverageFillPrice.Value, open.RemainingQuantity));
        var costs = PaperTradingCostModel.CalculateOptionCharges(transactions);
        var estimatedCosts = costs.Total;
        var pnl = grossPnl - estimatedCosts;
        var reason = exitReason.ToString();
        var db = services.GetRequiredService<TradingDbContext>();
        if (!await db.PaperTradeResults.AnyAsync(value => value.SignalId == open.Signal.SignalId,
                cancellationToken))
        {
            db.PaperTradeResults.Add(new PaperTradeResult(Guid.NewGuid(), open.Signal.SignalId,
                optionInstrument.Id, optionInstrument.TradingSymbol, open.TotalQuantity,
                entryPrice, exit.AverageFillPrice.Value, grossPnl,
                estimatedCosts, JsonSerializer.Serialize(costs), pnl, reason, timeProvider.GetUtcNow()));
            await db.SaveChangesAsync(cancellationToken);
        }
        var writer = services.GetRequiredService<IAuditWriter>();
        await writer.WriteAsync(new AuditEntry("paper-automation", "PaperTradeClosed", "Signal",
            open.Signal.SignalId.ToString("N"), reason, "{}", JsonSerializer.Serialize(new
            {
                entryPrice,
                exitPrice = exit.AverageFillPrice,
                quantity = open.TotalQuantity,
                addOn = open.AddOn is null ? null : new { open.AddOn.AverageFillPrice,
                    open.AddOn.FilledQuantity },
                grossPnl,
                estimatedCosts,
                costBreakdown = costs,
                realisedPnl = pnl,
                targetContinuation = new { enabled = options.Value.TargetExtensionEnabled,
                    entryRegime = open.Signal.Regime.ToString(), eligibleRegime = exitPlan.ExtendTarget,
                    underlyingFresh, behaviour = invalidation.Behaviour.ToString(),
                    directionAligned = invalidation.DirectionAligned,
                    confidence = open.Signal.Confidence,
                    minimumConfidence = options.Value.TargetExtensionMinimumConfidence,
                    confirmedInvalidation = invalidation.ShouldExit, allowed = extendBeyondTarget },
                reversalEvidenceScore = invalidation.EvidenceScore,
                reversalEvidence = invalidation.SupportingEvidence
            }), open.Signal.SignalId.ToString("N"), timeProvider.GetUtcNow()), cancellationToken);
        state.Record("TradeClosed", false, $"Paper trade closed by {reason}; realised P&L {pnl:F2}.",
            tradeState.TradesToday, tradeState.RealisedPnl + pnl);
        return false;
    }

    private async Task<UnderlyingTrendInvalidationResult> EvaluateUnderlyingInvalidationAsync(
        IServiceProvider services, OpenTrade open, Guid underlyingInstrumentId,
        DateTimeOffset sessionStart, DateTimeOffset sessionEnd, CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<TradingDbContext>();
        var interval = marketOptions.Value.CandleIntervalSeconds;
        var evaluatedAt = timeProvider.GetUtcNow();
        var completedBefore = evaluatedAt.AddSeconds(-interval);
        var candles = await db.Candles.AsNoTracking().Where(value =>
                value.InstrumentId == underlyingInstrumentId && value.Source == "Groww" &&
                value.IntervalSeconds == interval && value.OpenTimeUtc >= sessionStart &&
                value.OpenTimeUtc < sessionEnd && value.OpenTimeUtc <= completedBefore)
            .OrderBy(value => value.OpenTimeUtc).ToListAsync(cancellationToken);
        if (candles.Count < 21)
            return new(false, 0m, ["At least 21 completed Nifty candles are required."]);
        if (evaluatedAt - candles[^1].OpenTimeUtc.AddSeconds(interval) >
            TimeSpan.FromSeconds(interval + marketOptions.Value.MaximumAgeSeconds))
            return new(false, 0m, ["Completed Nifty candles are stale."]);

        var postEntryCompleted = candles.Count(value =>
            value.OpenTimeUtc.AddSeconds(value.IntervalSeconds) > open.Signal.MarketDataTimestampUtc);
        if (postEntryCompleted < 2)
            return new(false, 0m, ["Waiting for two completed Nifty candles after entry."]);

        var closes = candles.Select(value => value.Close).ToArray();
        var bars = candles.Select(value => new StrategyPriceBar(
            value.OpenTimeUtc, value.Open, value.High, value.Low, value.Close)).ToArray();
        return UnderlyingTrendInvalidationPolicy.Evaluate(open.Signal.Direction, bars,
            Ema(closes, 9), Ema(closes, 21), options.Value.MinimumReversalStructureStrength,
            options.Value.RequiredReversalEvidenceCount);
    }

    private async Task CaptureExitResearchAsync(IServiceProvider services,
        TradingDbContext db, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var oldest = now.AddMinutes(-60 - options.Value.ExitResearchMaximumDelayMinutes);
        var trades = await db.PaperTradeResults.AsNoTracking().Where(value =>
                value.ClosedAtUtc >= oldest && value.ClosedAtUtc <= now.AddMinutes(-5))
            .ToListAsync(cancellationToken);
        if (trades.Count == 0) return;

        var gateway = services.GetRequiredService<IGrowwReadOnlyGateway>();
        foreach (var trade in trades)
        {
            foreach (var horizon in PaperResearchCadence.PostExitHorizonMinutes)
            {
                var scheduled = trade.ClosedAtUtc.AddMinutes(horizon);
                if (now < scheduled || now > scheduled.AddMinutes(options.Value.ExitResearchMaximumDelayMinutes))
                    continue;
                if (await db.PaperExitFollowUps.AnyAsync(value =>
                        value.TradeResultId == trade.Id && value.HorizonMinutes == horizon,
                        cancellationToken)) continue;

                var instrument = await db.Instruments.AsNoTracking().SingleOrDefaultAsync(value =>
                    value.Id == trade.InstrumentId, cancellationToken);
                if (instrument is null) continue;
                var quote = await gateway.GetQuoteAsync(new GrowwQuoteRequest(
                    instrument.Exchange, "FNO", instrument.TradingSymbol), cancellationToken);
                var observedPrice = quote.BidPrice is > 0 ? quote.BidPrice.Value : quote.LastPrice;
                if (observedPrice <= 0) continue;
                db.PaperExitFollowUps.Add(new PaperExitFollowUp(Guid.NewGuid(), trade.Id,
                    trade.SignalId, horizon, observedPrice,
                    (observedPrice - trade.EntryPrice) * trade.Quantity,
                    (observedPrice - trade.ExitPrice) * trade.Quantity,
                    scheduled, now));
                await db.SaveChangesAsync(cancellationToken);
            }
        }
    }

    private async Task PersistTradePriceSampleAsync(TradingDbContext db, Guid signalId,
        Guid instrumentId, decimal price, CancellationToken cancellationToken)
    {
        if (price <= 0) return;
        var now = timeProvider.GetUtcNow();
        var sampleBucket = PaperTradePriceSample.BucketFor(now);
        if (await db.PaperTradePriceSamples.AsNoTracking().AnyAsync(value =>
                value.SignalId == signalId && value.ObservedBucketUtc == sampleBucket,
                cancellationToken)) return;

        db.PaperTradePriceSamples.Add(new PaperTradePriceSample(Guid.NewGuid(), signalId,
            instrumentId, price, now));
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task PersistProfitProtectionSnapshotAsync(TradingDbContext db, OpenTrade open,
        Guid underlyingInstrumentId, decimal currentPrice, decimal effectiveStop,
        decimal peakPrice, DateTimeOffset peakAtUtc, decimal worstPrice,
        DateTimeOffset worstAtUtc, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var minute = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0,
            TimeSpan.Zero);
        var outcome = $"ProfitProtection:{open.Signal.SignalId:N}:{minute:yyyyMMddHHmm}";
        if (await db.MarketStrategyAudits.AsNoTracking().AnyAsync(value =>
                value.Market == "nifty" && value.Outcome == outcome, cancellationToken)) return;

        var entry = open.AverageEntryPrice;
        var multiplier = open.Entry.Direction == Direction.Buy ? 1m : -1m;
        var initialRisk = Math.Abs(entry - open.StopLoss);
        var peakGrossPnl = (peakPrice - entry) * open.TotalQuantity * multiplier;
        var worstGrossPnl = (worstPrice - entry) * open.TotalQuantity * multiplier;
        var currentGrossPnl = (currentPrice - entry) * open.TotalQuantity * multiplier;
        var remainingUnrealisedPnl = (currentPrice - entry) * open.RemainingQuantity * multiplier;
        var favourableR = initialRisk <= 0m ? 0m : Math.Max(0m,
            (peakPrice - entry) * multiplier / initialRisk);
        var exitPlan = ProfessionalExitPolicy.For(ProfessionalExitPolicy.From(open.Signal.Regime));
        var stage = effectiveStop == open.StopLoss ? "InitialRisk"
            : (effectiveStop - entry) * multiplier <= 0m ? "BreakEven"
            : favourableR >= exitPlan.BreakEvenTriggerR + exitPlan.TrailDistanceR ? "Trailing"
            : "ProfitLocked";
        db.MarketStrategyAudits.Add(new(Guid.NewGuid(), "nifty", underlyingInstrumentId,
            minute, outcome, 0m, JsonSerializer.Serialize(new
            {
                signalId = open.Signal.SignalId,
                currentPrice,
                currentGrossPnl,
                remainingUnrealisedPnl,
                initialStop = open.StopLoss,
                effectiveStop,
                protectionStage = stage,
                favourableR,
                peakPrice,
                peakGrossPnl,
                peakAtUtc,
                worstPrice,
                worstGrossPnl,
                worstAtUtc,
                grossProfitGiveback = Math.Max(0m, peakGrossPnl - currentGrossPnl),
                observedAtUtc = now
            })));
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task TracePaperProtectionAsync(TradingDbContext db, Guid signalId, Guid underlyingId,
        decimal effectiveStop, int remainingQuantity, DateTimeOffset at, CancellationToken token)
    {
        // Evidence only; the trading worker never reads this outcome to manage orders.
        var audit = new MarketStrategyAudit(Guid.NewGuid(), "nifty", underlyingId, at,
            $"PaperStopSize:{signalId:N}", 0m, JsonSerializer.Serialize(new { effectiveStop, remainingQuantity }));
        try { db.MarketStrategyAudits.Add(audit); await db.SaveChangesAsync(token); }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            db.Entry(audit).State = EntityState.Detached;
            LogProtectionTraceUnavailable(logger, signalId, ex.GetType().Name);
        }
    }

    [LoggerMessage(LogLevel.Warning, "Paper protection trace unavailable for {sourceId}: {failureType}.")]
    private static partial void LogProtectionTraceUnavailable(ILogger logger, Guid sourceId, string failureType);

    private async Task<decimal> PersistLiveProtectionDesiredAsync(TradingDbContext db, Guid signalId,
        Guid underlyingInstrumentId, Direction direction, decimal initialStop, decimal tickSize, decimal effectiveStop,
        int remainingQuantity, CancellationToken cancellationToken)
    {
        var outcome = $"LiveProtectionDesired:{signalId:N}";
        var previous = await db.MarketStrategyAudits.AsNoTracking().Where(value =>
                value.Market == TradingMarketCatalog.Nifty.Code && value.Outcome == outcome)
            .OrderByDescending(value => value.CandleTimeUtc)
            .Select(value => value.ReasonsJson).FirstOrDefaultAsync(cancellationToken);
        var recorded = AutomaticLiveExecutionService.TryReadDesiredStop(previous, out var priorStop);
        effectiveStop = PaperProtectiveStopPolicy.ExecutableTrail(direction, effectiveStop,
            recorded ? priorStop : initialStop, tickSize);
        if (recorded && priorStop == effectiveStop) return effectiveStop;
        var now = timeProvider.GetUtcNow();
        db.MarketStrategyAudits.Add(new MarketStrategyAudit(Guid.NewGuid(),
            TradingMarketCatalog.Nifty.Code, underlyingInstrumentId, now, outcome, 0m,
            JsonSerializer.Serialize(new { signalId, effectiveStop, remainingQuantity, observedAtUtc = now })));
        await db.SaveChangesAsync(cancellationToken);
        return effectiveStop;
    }

    private async Task PersistDeferredDirectionalStopAsync(TradingDbContext db, OpenTrade open,
        Guid underlyingInstrumentId, decimal currentPrice, decimal effectiveStop,
        decimal adverseRiskMultiple, IReadOnlyList<string> reasons,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var minute = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0,
            TimeSpan.Zero);
        var outcome = $"DirectionalStopDeferred:{open.Signal.SignalId:N}:{minute:yyyyMMddHHmm}";
        if (await db.MarketStrategyAudits.AsNoTracking().AnyAsync(value =>
                value.Market == "nifty" && value.Outcome == outcome, cancellationToken)) return;
        db.MarketStrategyAudits.Add(new(Guid.NewGuid(), "nifty", underlyingInstrumentId,
            minute, outcome, open.Signal.Confidence, JsonSerializer.Serialize(new
            {
                signalId = open.Signal.SignalId,
                currentPrice,
                initialStop = open.StopLoss,
                effectiveStop,
                adverseRiskMultiple,
                reasons,
                paperOnly = true,
                observedAtUtc = now
            })));
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<RebuiltTradeState> RebuildTradeStateAsync(TradingDbContext db,
        IReadOnlyList<Signal> signals, DateTimeOffset sessionStart, DateTimeOffset sessionEnd,
        CancellationToken cancellationToken)
    {
        var open = new List<OpenTrade>();
        var count = 0;
        var realised = 0m;
        var closedToday = new List<(DateTimeOffset ExitAtUtc, decimal NetPnl)>();
        foreach (var row in signals.OrderBy(value => value.MarketDataTimestampUtc))
        {
            var entry = await broker.GetOrderAsync($"{row.Id:N}-ENTRY", cancellationToken);
            if (entry?.State != OrderState.Filled) continue;
            if (row.MarketDataTimestampUtc >= sessionStart && row.MarketDataTimestampUtc < sessionEnd)
                count++;
            var signal = ToStrategySignal(row);
            var addOn = await broker.GetOrderAsync($"{row.Id:N}-ADDON", cancellationToken);
            if (addOn?.State != OrderState.Filled) addOn = null;
            var exit = await broker.GetOrderAsync($"{row.Id:N}-EXIT", cancellationToken);
            if (exit?.State == OrderState.Filled)
            {
                var multiplier = entry.Direction == Direction.Buy ? 1m : -1m;
                var partial = await broker.GetOrderAsync($"{row.Id:N}-PARTIAL", cancellationToken);
                var totalQuantity = entry.FilledQuantity + (addOn?.FilledQuantity ?? 0);
                var averageEntry = ((entry.AverageFillPrice!.Value * entry.FilledQuantity) +
                    (addOn?.AverageFillPrice.GetValueOrDefault() ?? 0m) *
                    (addOn?.FilledQuantity ?? 0)) / totalQuantity;
                var grossPnl = (exit.AverageFillPrice!.Value - averageEntry) *
                               exit.FilledQuantity * multiplier;
                if (partial?.State == OrderState.Filled && partial.AverageFillPrice is { } bookedPrice)
                    grossPnl += (bookedPrice - averageEntry) *
                                partial.FilledQuantity * multiplier;
                if (row.MarketDataTimestampUtc >= sessionStart && row.MarketDataTimestampUtc < sessionEnd)
                {
                    var transactions = new List<PaperOptionTransaction>
                    {
                        new(entry.Direction == Direction.Buy ? PaperOptionTransactionSide.Buy :
                            PaperOptionTransactionSide.Sell, entry.AverageFillPrice.Value,
                            entry.FilledQuantity)
                    };
                    if (addOn?.AverageFillPrice is { } addOnPrice)
                        transactions.Add(new(addOn.Direction == Direction.Buy ?
                            PaperOptionTransactionSide.Buy : PaperOptionTransactionSide.Sell,
                            addOnPrice, addOn.FilledQuantity));
                    if (partial?.State == OrderState.Filled && partial.AverageFillPrice is { } partialPrice)
                        transactions.Add(new(partial.Direction == Direction.Buy ?
                            PaperOptionTransactionSide.Buy : PaperOptionTransactionSide.Sell,
                            partialPrice, partial.FilledQuantity));
                    transactions.Add(new(exit.Direction == Direction.Buy ? PaperOptionTransactionSide.Buy :
                        PaperOptionTransactionSide.Sell, exit.AverageFillPrice.Value,
                        exit.FilledQuantity));
                    var netPnl = grossPnl - PaperTradingCostModel.CalculateOptionCharges(transactions).Total;
                    realised += netPnl;
                    closedToday.Add((exit.UpdatedAtUtc, netPnl));
                }
            }
            else
            {
                var risk = await db.RiskDecisions.AsNoTracking()
                    .SingleOrDefaultAsync(value => value.SignalId == row.Id, cancellationToken);
                var protective = ParseProtectivePrices(risk?.SnapshotJson,
                    signal.ProposedStopLoss, signal.ProposedTarget);
                var partial = await broker.GetOrderAsync($"{row.Id:N}-PARTIAL", cancellationToken);
                var remaining = entry.FilledQuantity + (addOn?.FilledQuantity ?? 0) -
                    (partial?.State == OrderState.Filled ? partial.FilledQuantity : 0);
                if (remaining > 0)
                    open.Add(new OpenTrade(signal, entry, protective.StopLoss, protective.Target,
                        remaining, partial?.State == OrderState.Filled ? partial : null, addOn));
            }
        }
        var orderedClosed = closedToday.OrderBy(value => value.ExitAtUtc).ToArray();
        var consecutiveLosses = orderedClosed.Reverse().TakeWhile(value => value.NetPnl < 0).Count();
        var lastLossAtUtc = orderedClosed.Where(value => value.NetPnl < 0)
            .Select(value => (DateTimeOffset?)value.ExitAtUtc).LastOrDefault();
        return new(count, realised, consecutiveLosses, lastLossAtUtc, open);
    }

    private static async Task<IReadOnlyList<Signal>> LoadSessionSignalsAsync(TradingDbContext db,
        Guid instrumentId, DateTimeOffset start, DateTimeOffset end, CancellationToken token)
    {
        return await db.Signals.AsNoTracking().Where(value => value.InstrumentId == instrumentId &&
            value.MarketDataTimestampUtc >= start && value.MarketDataTimestampUtc < end)
            .ToListAsync(token);
    }

    private async Task UpdateReadinessAsync(TradingDbContext db, Guid indexInstrumentId,
        DateTimeOffset sessionStart, DateTimeOffset sessionEnd, DateTimeOffset now,
        DateTimeOffset indiaNow, bool killSwitchActive, CancellationToken cancellationToken)
    {
        var interval = marketOptions.Value.CandleIntervalSeconds;
        var indexCount = await db.Candles.AsNoTracking().CountAsync(value =>
            value.InstrumentId == indexInstrumentId && value.Source == "Groww" &&
            value.IntervalSeconds == interval && value.OpenTimeUtc >= sessionStart &&
            value.OpenTimeUtc < sessionEnd, cancellationToken);
        var priorClose = await db.Candles.AsNoTracking().AnyAsync(value =>
            value.InstrumentId == indexInstrumentId && value.OpenTimeUtc < sessionStart,
            cancellationToken);
        var date = DateOnly.FromDateTime(indiaNow.Date);
        var future = await db.Instruments.AsNoTracking().Where(value =>
                value.Exchange == "NSE" && value.Type == InstrumentType.Future &&
                value.TradingSymbol.StartsWith("NIFTY") && value.IsActive &&
                value.ExpiryDate >= date)
            .OrderBy(value => value.ExpiryDate).FirstOrDefaultAsync(cancellationToken);
        var futureCount = future is null ? 0 : await db.Candles.AsNoTracking().CountAsync(value =>
            value.InstrumentId == future.Id && value.Source == "Groww" &&
            value.IntervalSeconds == interval && value.OpenTimeUtc >= sessionStart &&
            value.OpenTimeUtc < sessionEnd, cancellationToken);
        var latestReceived = await db.MarketObservations.AsNoTracking().Where(value =>
                value.InstrumentId == indexInstrumentId && value.Source == "Groww")
            .OrderByDescending(value => value.ReceivedAtUtc)
            .Select(value => (DateTimeOffset?)value.ReceivedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        var fresh = latestReceived is not null && now - latestReceived <=
            TimeSpan.FromSeconds(marketOptions.Value.MaximumAgeSeconds);
        var localTime = TimeOnly.FromTimeSpan(indiaNow.TimeOfDay);
        var start = ParseTime(options.Value.EntryWindowStart);
        var cutoff = ParseTime(options.Value.EntryCutoff);
        var futuresDetail = future is null
            ? "Nifty futures instrument not synchronised"
            : localTime < new TimeOnly(9, 15)
                ? $"Market opens at 09:15 IST; {future.TradingSymbol} confirmation will then build"
                : $"{future.TradingSymbol}: {futureCount}/{options.Value.MinimumFuturesConfirmationCandles} candles";
        state.RecordReadiness([
            new("index", "Nifty spot feed", indexCount > 0,
                indexCount > 0 ? $"{indexCount} completed candles" : "Waiting for spot candles"),
            new("history", "Previous-session context", priorClose,
                priorClose ? "Previous close available" : "Historical backfill pending"),
            new("future", "Nifty futures confirmation",
                futureCount >= options.Value.MinimumFuturesConfirmationCandles,
                futuresDetail),
            new("freshness", "Live data freshness", fresh,
                fresh ? "Latest Nifty quote is current" : "Live Nifty quote is stale"),
            new("window", "Entry window", IndexEntryWindowPolicy.AllowsEntry(localTime, start, cutoff),
                $"{options.Value.EntryWindowStart}–{(cutoff < IndexEntryWindowPolicy.LastEntryCutoff ? cutoff : IndexEntryWindowPolicy.LastEntryCutoff):HH:mm} IST"),
            new("risk", "Risk controls", !killSwitchActive,
                killSwitchActive ? "Kill switch active" : "Kill switch clear")
        ]);
    }

    private static StrategySignal ToStrategySignal(Signal value)
        => NiftySignalRecoveryPolicy.Recover(value);

    private static (decimal StopLoss, decimal Target) ParseProtectivePrices(
        string? snapshotJson, decimal fallbackStop, decimal fallbackTarget)
    {
        if (string.IsNullOrWhiteSpace(snapshotJson)) return (fallbackStop, fallbackTarget);
        using var document = JsonDocument.Parse(snapshotJson);
        var root = document.RootElement;
        var stop = root.TryGetProperty("finalStopLoss", out var stopElement)
            ? stopElement.GetDecimal() : fallbackStop;
        var target = root.TryGetProperty("finalTarget", out var targetElement)
            ? targetElement.GetDecimal() : fallbackTarget;
        return (stop, target);
    }

    private async Task PersistStrategyEvaluationAsync(TradingDbContext db,
        ITradingStrategy strategy, Guid instrumentId, DateTimeOffset candleTimeUtc,
        decimal currentPrice, decimal openingRangeHigh, decimal openingRangeLow,
        decimal vwap, decimal fastEma, decimal slowEma, decimal atrPercent,
        decimal relativeFuturesVolume, MarketRegimeResult regime, string outcome,
        IReadOnlyList<string> failedConditions, StrategySignal? signal,
        NiftyOptionContractCandidate? option, decimal? optionPremium,
        CancellationToken cancellationToken,
        MarketStructureQualitySnapshot? shadowStructure = null)
    {
        var recordedStrategyId = signal?.StrategyId ?? strategy.StrategyId;
        var recordedStrategyVersion = signal?.StrategyVersion ?? strategy.Version;
        // Keep every decision stage: the unique evaluation row retains only the first stage.
        var auditOutcome = $"EntryStage:{outcome}";
        if (!await db.MarketStrategyAudits.AnyAsync(value => value.Market == "nifty" &&
                value.CandleTimeUtc == candleTimeUtc && value.Outcome == auditOutcome, cancellationToken))
        {
            db.MarketStrategyAudits.Add(new(Guid.NewGuid(), "nifty", instrumentId,
                candleTimeUtc, auditOutcome, signal?.Confidence ?? 0m,
                JsonSerializer.Serialize(new { strategy = recordedStrategyId, outcome, reasons = failedConditions,
                    signalId = signal?.SignalId, observedAtUtc = timeProvider.GetUtcNow() })));
            await db.SaveChangesAsync(cancellationToken);
        }
        if (await db.StrategyEvaluations.AnyAsync(value =>
                value.StrategyCode == recordedStrategyId &&
                value.InstrumentId == instrumentId &&
                value.CandleTimeUtc == candleTimeUtc, cancellationToken))
            return;

        var evaluation = new StrategyEvaluation(Guid.NewGuid(), recordedStrategyId,
            recordedStrategyVersion, instrumentId, candleTimeUtc, currentPrice, openingRangeHigh,
            openingRangeLow, vwap, fastEma, slowEma, atrPercent, relativeFuturesVolume,
            regime.Regime, regime.DirectionalBias, regime.Confidence, outcome,
            JsonSerializer.Serialize(failedConditions), signal?.SignalId,
            option?.TradingSymbol, option?.Type.ToString(), option?.ExpiryDate,
            option?.StrikePrice, optionPremium, timeProvider.GetUtcNow(),
            shadowStructure?.State.ToString(), shadowStructure?.TrendQuality,
            shadowStructure?.WouldPermit,
            shadowStructure is null ? null : JsonSerializer.Serialize(shadowStructure.Reasons));
        db.StrategyEvaluations.Add(evaluation);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
               { SqlState: PostgresErrorCodes.UniqueViolation,
                 ConstraintName: "IX_strategy_evaluations_StrategyCode_InstrumentId_CandleTimeUtc" })
        {
            // Concurrent cycles may persist the same completed-candle decision. The
            // database uniqueness boundary is authoritative and the duplicate is not a
            // failed trading cycle.
            db.Entry(evaluation).State = EntityState.Detached;
        }
    }

    private static void RecordPortfolioRisk(PaperAutomationState automationState,
        RebuiltTradeState tradeState, decimal maximumDailyLoss, bool reconciliationHealthy)
    {
        var marks = automationState.GetCurrent().ActivePositionMarks ?? [];
        var unrealised = marks.Sum(mark => mark.UnrealisedPnl.GetValueOrDefault());
        automationState.RecordPortfolioRisk(
            tradeState.OpenPositions.Count,
            tradeState.OpenPositions.Sum(open =>
                open.AverageEntryPrice * open.RemainingQuantity),
            tradeState.OpenPositions.Sum(open =>
                Math.Abs(open.AverageEntryPrice - open.StopLoss) *
                open.RemainingQuantity),
            Math.Max(0m, -(tradeState.RealisedPnl + Math.Min(0m, unrealised))),
            maximumDailyLoss,
            marks.Count(mark => !mark.QuoteAvailable),
            reconciliationHealthy);
    }

    private static decimal Ema(decimal[] values, int period)
    {
        var multiplier = 2m / (period + 1m);
        var result = values[0];
        for (var index = 1; index < values.Length; index++)
            result = (values[index] - result) * multiplier + result;
        return result;
    }

    private static TimeOnly ParseTime(string value) => TimeOnly.ParseExact(value, "HH:mm");
    private static DateTimeOffset ToUtc(DateTime date, TimeOnly time) =>
        new DateTimeOffset(date.Year, date.Month, date.Day, time.Hour, time.Minute, 0,
            IndiaTimeZone.GetUtcOffset(date)).ToUniversalTime();
    private static Guid DeterministicSessionId(DateOnly date)
    {
        var bytes = new byte[16];
        BitConverter.GetBytes(date.DayNumber).CopyTo(bytes, 0);
        bytes[15] = 7;
        return new Guid(bytes);
    }

    private static TimeZoneInfo FindIndiaTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("India Standard Time"); }
    }

    private sealed record OpenTrade(StrategySignal Signal, BrokerOrderSnapshot Entry,
        decimal StopLoss, decimal Target, int RemainingQuantity, BrokerOrderSnapshot? PartialExit,
        BrokerOrderSnapshot? AddOn)
    {
        public int TotalQuantity => Entry.FilledQuantity + (AddOn?.FilledQuantity ?? 0);
        public decimal AverageEntryPrice
        {
            get
            {
                var addOnValue = AddOn?.AverageFillPrice is { } price
                    ? price * AddOn.FilledQuantity : 0m;
                return (Entry.AverageFillPrice!.Value * Entry.FilledQuantity + addOnValue) /
                       TotalQuantity;
            }
        }
    }
    private sealed record RebuiltTradeState(int TradesToday, decimal RealisedPnl,
        int ConsecutiveLosses, DateTimeOffset? LastLossAtUtc, IReadOnlyList<OpenTrade> OpenPositions);

    [LoggerMessage(Level = LogLevel.Error, Message = "Automated paper-trading cycle failed.")]
    private static partial void LogCycleFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Optional paper reversal-exit research capture failed; trading continues.")]
    private static partial void LogExitResearchCaptureFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Live option quote failed for active paper position {TradingSymbol}.")]
    private static partial void LogPositionQuoteFailed(ILogger logger, string tradingSymbol,
        Exception exception);
}
