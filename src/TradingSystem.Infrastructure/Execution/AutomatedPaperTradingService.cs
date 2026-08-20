using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
    ILogger<AutomatedPaperTradingService> logger) : BackgroundService
{
    private static readonly TimeZoneInfo IndiaTimeZone = FindIndiaTimeZone();
    private DateTimeOffset? lastEvaluatedCandleUtc;
    private readonly Dictionary<Guid, decimal> favourablePriceBySignal = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            state.Record("Disabled", false, "Automated paper trading is disabled by server configuration.");
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
                state.Record("Faulted", false, "Automation cycle failed; new entries are blocked.");
                LogCycleFailed(logger, exception);
            }

            await Task.Delay(TimeSpan.FromSeconds(options.Value.EvaluationIntervalSeconds), stoppingToken);
        }
    }

    private async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var live = scope.ServiceProvider.GetRequiredService<IOptions<LiveNiftyOptions>>().Value;
        var instrument = await db.Instruments.AsNoTracking().SingleOrDefaultAsync(value =>
            value.Exchange == live.Exchange && value.TradingSymbol == live.TradingSymbol &&
            value.Segment == InstrumentSegment.Cash && value.IsActive, cancellationToken);
        if (instrument is null)
        {
            state.Record("Blocked", false, "Nifty instrument is not synchronised.");
            return;
        }

        try
        {
            await CaptureReversalExitResearchAsync(scope.ServiceProvider, db, cancellationToken);
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
        var dailyLossOverrideJson = await db.ApplicationSettings.AsNoTracking().Where(value =>
                value.Mode == TradingMode.Paper && value.Key == EfPaperDailyLossOverrideService.Key)
            .Select(value => value.ValueJson).SingleOrDefaultAsync(cancellationToken);
        var dailyLossLimitOverridden = EfPaperDailyLossOverrideService.IsActiveForDate(
            dailyLossOverrideJson, DateOnly.FromDateTime(indiaNow.Date));
        await UpdateReadinessAsync(db, instrument.Id, sessionStart, sessionEnd, now, indiaNow,
            killSwitchActive, cancellationToken);
        var tradeState = await RebuildTradeStateAsync(db, recoverySignals, sessionStart, sessionEnd,
            cancellationToken);
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
        if (localTime < entryWindowStart || localTime >= entryCutoff)
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
        if (candles.Count < options.Value.MinimumSignalCandles)
        {
            state.Record("WarmingUp", false,
                $"Waiting for minimum pattern data: {candles.Count}/{options.Value.MinimumSignalCandles} completed candles.",
                tradeState.TradesToday, tradeState.RealisedPnl);
            return;
        }

        var sessionDate = DateOnly.FromDateTime(indiaNow.Date);
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
        var confirmationCandles = await db.Candles.AsNoTracking().Where(value =>
                value.InstrumentId == confirmationFuture.Id && value.Source == "Groww" &&
                value.IntervalSeconds == marketOptions.Value.CandleIntervalSeconds &&
                value.OpenTimeUtc >= sessionStart && value.OpenTimeUtc < sessionEnd)
            .OrderBy(value => value.OpenTimeUtc).ToListAsync(cancellationToken);
        if (confirmationCandles.Count < options.Value.MinimumFuturesConfirmationCandles)
        {
            state.Record("WarmingUp", false,
                $"Waiting for minimum Nifty futures data: {confirmationCandles.Count}/{options.Value.MinimumFuturesConfirmationCandles} completed candles.",
                tradeState.TradesToday, tradeState.RealisedPnl);
            return;
        }

        var latestCandle = candles[^1];
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

        var previousClose = await db.Candles.AsNoTracking().Where(value =>
                value.InstrumentId == instrument.Id && value.Source == "Groww" &&
                value.OpenTimeUtc < sessionStart)
            .OrderByDescending(value => value.OpenTimeUtc).Select(value => (decimal?)value.Close)
            .FirstOrDefaultAsync(cancellationToken);
        if (previousClose is null)
        {
            state.Record("WarmingUp", false,
                "Previous-session close is unavailable; one persisted session is required.",
                tradeState.TradesToday, tradeState.RealisedPnl);
            return;
        }

        var openingCandles = candles.Where(value =>
            TimeOnly.FromTimeSpan(TimeZoneInfo.ConvertTime(value.OpenTimeUtc, IndiaTimeZone).TimeOfDay) < openingRangeEnd).ToArray();
        var alignedConfirmation = confirmationCandles
            .Where(value => value.OpenTimeUtc <= latestCandle.OpenTimeUtc).ToArray();
        if (alignedConfirmation.Length < 2)
        {
            state.Record("WarmingUp", false,
                "Nifty futures candles are not yet aligned with the Nifty signal candle.",
                tradeState.TradesToday, tradeState.RealisedPnl);
            return;
        }
        var totalVolume = alignedConfirmation.Sum(value => value.Volume);
        var averageVolume = alignedConfirmation.Length > 1
            ? alignedConfirmation.Take(alignedConfirmation.Length - 1)
                .Average(value => (decimal)value.Volume)
            : 0m;
        if (openingCandles.Length == 0 || totalVolume <= 0 || averageVolume <= 0)
        {
            state.Record("DataIncomplete", false,
                "Validated volume or opening-range data is unavailable; the strategy fails closed.",
                tradeState.TradesToday, tradeState.RealisedPnl);
            return;
        }

        var relativeVolume = alignedConfirmation[^1].Volume / averageVolume;
        var confirmationVolume = alignedConfirmation.ToDictionary(value => value.OpenTimeUtc,
            value => value.Volume);
        var weightedSpotCandles = candles.Where(value => confirmationVolume.ContainsKey(value.OpenTimeUtc))
            .ToArray();
        var alignedVolume = weightedSpotCandles.Sum(value => confirmationVolume[value.OpenTimeUtc]);
        if (alignedVolume <= 0)
        {
            state.Record("DataIncomplete", false,
                "Spot prices and futures volume are not aligned; the strategy fails closed.",
                tradeState.TradesToday, tradeState.RealisedPnl);
            return;
        }
        var vwap = weightedSpotCandles.Sum(value =>
            ((value.High + value.Low + value.Close) / 3m) * confirmationVolume[value.OpenTimeUtc]) /
            alignedVolume;
        var closes = candles.Select(value => value.Close).ToArray();
        var fast = Ema(closes, 9);
        var slow = Ema(closes, 21);
        var atr = AtrPercent(candles.TakeLast(15).ToArray());
        var openingRangeHigh = openingCandles.Max(value => value.High);
        var openingRangeLow = openingCandles.Min(value => value.Low);
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
        var strategyContext = new StrategyEvaluationContext(instrument.Id,
            candleDecisionTime, latestCandle.Close,
            openingRangeHigh, openingRangeLow, relativeVolume,
            regime.Regime, regime.DirectionalBias, regime.Confidence, regime.TradingPermitted, true,
            sessionSignals.OrderByDescending(value => value.MarketDataTimestampUtc)
                .Select(value => (DateTimeOffset?)value.MarketDataTimestampUtc).FirstOrDefault(),
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
        var shadowStructure = MarketStructureQualityAnalyzer.Analyze(
            strategyContext.RecentCandles, null, strategyContext.CurrentPrice,
            strategyContext.Vwap, strategyContext.AtrPercent, strategyContext.OpeningRangeHigh,
            strategyContext.OpeningRangeLow);
        var strategyEvaluation = strategy.EvaluateDetailed(strategyContext);
        var signal = strategyEvaluation.Signal;
        if (signal is null)
        {
            var lastNoTradeAudit = await db.StrategyEvaluations.AsNoTracking()
                .Where(value => value.StrategyCode == strategy.StrategyId &&
                                value.InstrumentId == instrument.Id &&
                                value.Outcome == "NoSignal" &&
                                value.CandleTimeUtc >= sessionStart &&
                                value.CandleTimeUtc < sessionEnd)
                .OrderByDescending(value => value.CandleTimeUtc)
                .Select(value => (DateTimeOffset?)value.CandleTimeUtc)
                .FirstOrDefaultAsync(cancellationToken);
            if (StrategyEvaluationAuditCadence.IsNoTradeAuditDue(sessionStart,
                    candleDecisionTime, lastNoTradeAudit,
                    options.Value.NoTradeAuditIntervalMinutes))
            {
                await PersistStrategyEvaluationAsync(db, strategy, instrument.Id, candleDecisionTime,
                    latestCandle.Close, openingRangeHigh, openingRangeLow, vwap, fast, slow, atr,
                    relativeVolume, regime, "NoSignal", strategyEvaluation.FailedConditions,
                    null, null, null, cancellationToken, shadowStructure);
            }
            state.Record("Scanning", true,
                $"Scanning each completed candle across the price-action portfolio. " +
                $"{string.Join(" ", strategyEvaluation.FailedConditions)} " +
                $"No-trade research is stored every {options.Value.NoTradeAuditIntervalMinutes} minutes.",
                tradeState.TradesToday, tradeState.RealisedPnl);
            return;
        }

        shadowStructure = MarketStructureQualityAnalyzer.Analyze(
            strategyContext.RecentCandles, signal.Direction, strategyContext.CurrentPrice,
            strategyContext.Vwap, strategyContext.AtrPercent, strategyContext.OpeningRangeHigh,
            strategyContext.OpeningRangeLow, Math.Abs(signal.ProposedEntry - signal.ProposedStopLoss));

        var entryQuality = PaperEntryQualityPolicy.Evaluate(shadowStructure, signal.Direction);
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

        if (tradeState.OpenPositions.Count >= options.Value.MaximumConcurrentPositions)
        {
            state.Record("PortfolioCapacity", false,
                $"Concurrent paper-position capacity ({options.Value.MaximumConcurrentPositions}) is reached.",
                tradeState.TradesToday, tradeState.RealisedPnl);
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
                options.Value.OptionStrikeStep));
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
        var pricing = options.Value.PermissivePaperExecution
            ? OptionPaperTradePricing.ForPermissiveSimulation(quote)
            : OptionPaperTradePricing.Validate(quote,
                options.Value.MaximumOptionSpreadPercent, options.Value.MaximumOptionPremium,
                options.Value.MinimumOptionVolume, options.Value.MinimumOptionOpenInterest);
        if (!pricing.Approved)
        {
            var rejected = new RiskDecisionResult(false, 0, 0m, 0m,
                pricing.RejectionReasons, 0m, 0m);
            var rejectedProposal = new PaperOptionExecutionProposal(
                selectedOption.InstrumentId, selectedOption.TradingSymbol,
                selectedOption.Type.ToString(), selectedOption.ExpiryDate,
                selectedOption.StrikePrice, selectedOption.LotSize,
                options.Value.MaximumOptionLots, quote.OfferPrice ?? quote.LastPrice,
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

        var tradingDate = DateOnly.FromDateTime(indiaNow.Date);
        var stopLossPercent = ExpiryAwareOptionPolicy.StopLossPercent(tradingDate,
            selectedOption.ExpiryDate, options.Value.OptionStopLossPercent);
        var maximumLots = ExpiryAwareOptionPolicy.MaximumLots(tradingDate,
            selectedOption.ExpiryDate, options.Value.MaximumOptionLots);
        var protective = OptionPaperTradePricing.ProtectivePrices(pricing.EntryPrice,
            stopLossPercent, options.Value.OptionRewardToRiskRatio,
            selectedOption.TickSize);
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
        var maximumOptionQuantity = checked(selectedOption.LotSize * maximumLots);
        var riskOptions = scope.ServiceProvider.GetRequiredService<IOptions<PreliminaryRiskOptions>>().Value;
        var existingCapitalExposure = tradeState.OpenPositions.Sum(open =>
            open.Entry.AverageFillPrice.GetValueOrDefault() * open.RemainingQuantity);
        var openUnrealisedPnl = state.GetCurrent().ActivePositionMarks?.Sum(mark =>
            mark.UnrealisedPnl.GetValueOrDefault()) ?? 0m;
        var hardenedDecision = PaperPortfolioRiskPolicy.Evaluate(new(
            executionSignal.ProposedEntry, executionSignal.ProposedStopLoss,
            selectedOption.LotSize, maximumLots, riskOptions.MaximumRiskPerTrade,
            riskOptions.MaximumCapitalExposure, existingCapitalExposure,
            tradeState.RealisedPnl, openUnrealisedPnl, options.Value.MaximumDailyLoss,
            tradeState.OpenPositions.Count, options.Value.MaximumConcurrentPositions,
            killSwitchActive, true, fresh, dailyLossLimitOverridden));
        var decision = !hardenedDecision.Approved
            ? hardenedDecision with { FinalTarget = executionSignal.ProposedTarget }
            : options.Value.PermissivePaperExecution
                ? hardenedDecision with { FinalTarget = executionSignal.ProposedTarget }
                : scope.ServiceProvider.GetRequiredService<PreliminaryRiskEngine>().Evaluate(
                executionSignal, new RiskContext(now, tradeState.TradesToday, tradeState.OpenPositions.Count,
                    tradeState.RealisedPnl, options.Value.MaximumDailyLoss,
                    killSwitchActive, true, fresh, dailyLossLimitOverridden), selectedOption.LotSize,
                    Math.Min(maximumOptionQuantity, hardenedDecision.ApprovedQuantity));
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

        await PersistStrategyEvaluationAsync(db, strategy, instrument.Id, candleDecisionTime,
            latestCandle.Close, openingRangeHigh, openingRangeLow, vwap, fast, slow, atr,
            relativeVolume, regime, "PaperPositionOpened", [], signal, selectedOption,
            entry.AverageFillPrice, cancellationToken, shadowStructure);
        await PersistTradePriceSampleAsync(db, signal.SignalId, selectedOption.InstrumentId,
            entry.AverageFillPrice!.Value, cancellationToken);

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
        await PersistTradePriceSampleAsync(researchDb, open.Signal.SignalId,
            optionInstrument.Id, price, cancellationToken);

        var multiplier = open.Entry.Direction == Direction.Buy ? 1m : -1m;
        var entryPrice = open.Entry.AverageFillPrice!.Value;
        var favourable = favourablePriceBySignal.GetValueOrDefault(open.Signal.SignalId, entryPrice);
        favourable = open.Entry.Direction == Direction.Buy ? Math.Max(favourable, price) : Math.Min(favourable, price);
        favourablePriceBySignal[open.Signal.SignalId] = favourable;
        var effectiveStop = open.StopLoss;
        if (options.Value.BreakEvenEnabled || options.Value.TrailingStopEnabled)
            effectiveStop = PaperProtectiveStopPolicy.Calculate(open.Entry.Direction, entryPrice,
                open.StopLoss, favourable,
                options.Value.BreakEvenEnabled ? options.Value.BreakEvenTriggerRiskMultiple : decimal.MaxValue,
                options.Value.TrailingStopEnabled ? options.Value.TrailingStopRiskMultiple : decimal.MaxValue,
                options.Value.ProfitLockTriggerRiskMultiple,
                options.Value.ProfitLockRiskMultiple);
        var unrealised = (price - entryPrice) * open.RemainingQuantity * multiplier;

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

        var exitReason = PaperPositionExitPolicy.Evaluate(open.Entry.Direction, price,
            effectiveStop, open.Target,
            TimeOnly.FromTimeSpan(indiaTime), ParseTime(options.Value.ForcedExit), fresh);
        if (killSwitchActive && fresh) exitReason = PaperExitReason.EmergencyKillSwitch;
        if (exitReason == PaperExitReason.None && invalidation.ShouldExit)
            exitReason = PaperExitReason.UnderlyingTrendInvalidated;
        if (exitReason == PaperExitReason.ForcedIntradayExit && options.Value.WeeklyCarryForwardEnabled)
        {
            var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(
                timeProvider.GetUtcNow(), IndiaTimeZone).Date);
            var carry = WeeklyCarryPolicy.Evaluate(new WeeklyCarryInput(localDate,
                    optionInstrument.ExpiryDate, open.Entry.Direction, entryPrice, price,
                    open.Signal.Confidence, invalidation.ShouldExit, killSwitchActive),
                options.Value.WeeklyCarryMaximumDaysToExpiry,
                options.Value.WeeklyCarryMinimumConfidence);
            if (carry.Approved) exitReason = PaperExitReason.None;
        }

        var riskPerUnit = Math.Abs(entryPrice - open.StopLoss);
        if (exitReason == PaperExitReason.None && options.Value.PartialProfitEnabled && open.PartialExit is null &&
            ((open.Entry.Direction == Direction.Buy && price >= entryPrice + riskPerUnit * options.Value.PartialProfitRiskMultiple) ||
             (open.Entry.Direction == Direction.Sell && price <= entryPrice - riskPerUnit * options.Value.PartialProfitRiskMultiple)))
        {
            var partialQuantity = Math.Clamp((int)Math.Floor(open.Entry.FilledQuantity * options.Value.PartialExitFraction),
                1, Math.Max(1, open.Entry.FilledQuantity - 1));
            var partialReference = $"{open.Signal.SignalId:N}-PARTIAL";
            var partialDirection = open.Entry.Direction == Direction.Buy ? Direction.Sell : Direction.Buy;
            await broker.SubmitAsync(new(partialReference, open.Entry.InstrumentId, partialDirection,
                partialQuantity, price), cancellationToken);
            await paperControl.ProcessNextFillAsync(partialReference, cancellationToken);
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
                    : "Monitoring premium protection and confirmed Nifty trend invalidation." :
                    "Market data is stale; new entries are blocked and the paper position cannot be repriced.",
                tradeState.TradesToday, tradeState.RealisedPnl, unrealised, open.Signal.SignalId,
                open.Entry.Direction.ToString(), open.RemainingQuantity, open.Entry.AverageFillPrice,
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
        var grossPnl = (exit.AverageFillPrice!.Value - open.Entry.AverageFillPrice.Value) *
                       open.RemainingQuantity * multiplier;
        if (open.PartialExit?.AverageFillPrice is { } partialPrice)
            grossPnl += (partialPrice - open.Entry.AverageFillPrice.Value) *
                        open.PartialExit.FilledQuantity * multiplier;
        var transactions = new List<PaperOptionTransaction>
        {
            new(open.Entry.Direction == Direction.Buy ? PaperOptionTransactionSide.Buy :
                PaperOptionTransactionSide.Sell, open.Entry.AverageFillPrice.Value,
                open.Entry.FilledQuantity)
        };
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
                optionInstrument.Id, optionInstrument.TradingSymbol, open.Entry.FilledQuantity,
                open.Entry.AverageFillPrice.Value, exit.AverageFillPrice.Value, grossPnl,
                estimatedCosts, JsonSerializer.Serialize(costs), pnl, reason, timeProvider.GetUtcNow()));
            await db.SaveChangesAsync(cancellationToken);
        }
        var writer = services.GetRequiredService<IAuditWriter>();
        await writer.WriteAsync(new AuditEntry("paper-automation", "PaperTradeClosed", "Signal",
            open.Signal.SignalId.ToString("N"), reason, "{}", JsonSerializer.Serialize(new
            {
                entryPrice = open.Entry.AverageFillPrice,
                exitPrice = exit.AverageFillPrice,
                quantity = open.Entry.FilledQuantity,
                grossPnl,
                estimatedCosts,
                costBreakdown = costs,
                realisedPnl = pnl,
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
        var candles = await db.Candles.AsNoTracking().Where(value =>
                value.InstrumentId == underlyingInstrumentId && value.Source == "Groww" &&
                value.IntervalSeconds == interval && value.OpenTimeUtc >= sessionStart &&
                value.OpenTimeUtc < sessionEnd)
            .OrderBy(value => value.OpenTimeUtc).ToListAsync(cancellationToken);
        if (candles.Count < 21)
            return new(false, 0m, ["At least 21 completed Nifty candles are required."]);

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

    private async Task CaptureReversalExitResearchAsync(IServiceProvider services,
        TradingDbContext db, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var oldest = now.AddMinutes(-60 - options.Value.ExitResearchMaximumDelayMinutes);
        var trades = await db.PaperTradeResults.AsNoTracking().Where(value =>
                value.ExitReason == PaperExitReason.UnderlyingTrendInvalidated.ToString() &&
                value.ClosedAtUtc >= oldest && value.ClosedAtUtc <= now.AddMinutes(-15))
            .ToListAsync(cancellationToken);
        if (trades.Count == 0) return;

        var gateway = services.GetRequiredService<IGrowwReadOnlyGateway>();
        foreach (var trade in trades)
        {
            foreach (var horizon in new[] { 15, 30, 60 })
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
        var minute = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0,
            TimeSpan.Zero);
        if (await db.PaperTradePriceSamples.AsNoTracking().AnyAsync(value =>
                value.SignalId == signalId && value.ObservedMinuteUtc == minute,
                cancellationToken)) return;

        db.PaperTradePriceSamples.Add(new PaperTradePriceSample(Guid.NewGuid(), signalId,
            instrumentId, price, now));
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<RebuiltTradeState> RebuildTradeStateAsync(TradingDbContext db,
        IReadOnlyList<Signal> signals, DateTimeOffset sessionStart, DateTimeOffset sessionEnd,
        CancellationToken cancellationToken)
    {
        var open = new List<OpenTrade>();
        var count = 0;
        var realised = 0m;
        foreach (var row in signals.OrderBy(value => value.MarketDataTimestampUtc))
        {
            var entry = await broker.GetOrderAsync($"{row.Id:N}-ENTRY", cancellationToken);
            if (entry?.State != OrderState.Filled) continue;
            if (row.MarketDataTimestampUtc >= sessionStart && row.MarketDataTimestampUtc < sessionEnd)
                count++;
            var signal = ToStrategySignal(row);
            var exit = await broker.GetOrderAsync($"{row.Id:N}-EXIT", cancellationToken);
            if (exit?.State == OrderState.Filled)
            {
                var multiplier = entry.Direction == Direction.Buy ? 1m : -1m;
                var partial = await broker.GetOrderAsync($"{row.Id:N}-PARTIAL", cancellationToken);
                var grossPnl = (exit.AverageFillPrice!.Value - entry.AverageFillPrice!.Value) *
                               exit.FilledQuantity * multiplier;
                if (partial?.State == OrderState.Filled && partial.AverageFillPrice is { } bookedPrice)
                    grossPnl += (bookedPrice - entry.AverageFillPrice.Value) *
                                partial.FilledQuantity * multiplier;
                if (row.MarketDataTimestampUtc >= sessionStart && row.MarketDataTimestampUtc < sessionEnd)
                {
                    var transactions = new List<PaperOptionTransaction>
                    {
                        new(entry.Direction == Direction.Buy ? PaperOptionTransactionSide.Buy :
                            PaperOptionTransactionSide.Sell, entry.AverageFillPrice.Value,
                            entry.FilledQuantity)
                    };
                    if (partial?.State == OrderState.Filled && partial.AverageFillPrice is { } partialPrice)
                        transactions.Add(new(partial.Direction == Direction.Buy ?
                            PaperOptionTransactionSide.Buy : PaperOptionTransactionSide.Sell,
                            partialPrice, partial.FilledQuantity));
                    transactions.Add(new(exit.Direction == Direction.Buy ? PaperOptionTransactionSide.Buy :
                        PaperOptionTransactionSide.Sell, exit.AverageFillPrice.Value,
                        exit.FilledQuantity));
                    realised += grossPnl - PaperTradingCostModel.CalculateOptionCharges(transactions).Total;
                }
            }
            else
            {
                var risk = await db.RiskDecisions.AsNoTracking()
                    .SingleOrDefaultAsync(value => value.SignalId == row.Id, cancellationToken);
                var protective = ParseProtectivePrices(risk?.SnapshotJson,
                    signal.ProposedStopLoss, signal.ProposedTarget);
                var partial = await broker.GetOrderAsync($"{row.Id:N}-PARTIAL", cancellationToken);
                var remaining = entry.FilledQuantity - (partial?.State == OrderState.Filled ? partial.FilledQuantity : 0);
                if (remaining > 0)
                    open.Add(new OpenTrade(signal, entry, protective.StopLoss, protective.Target,
                        remaining, partial?.State == OrderState.Filled ? partial : null));
            }
        }
        return new(count, realised, open);
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
            new("window", "Entry window", localTime >= start && localTime < cutoff,
                $"{options.Value.EntryWindowStart}–{options.Value.EntryCutoff} IST"),
            new("risk", "Risk controls", !killSwitchActive,
                killSwitchActive ? "Kill switch active" : "Kill switch clear")
        ]);
    }

    private static StrategySignal ToStrategySignal(Signal value)
    {
        var fingerprint = value.Fingerprint.Split(':', StringSplitOptions.RemoveEmptyEntries);
        var strategyId = fingerprint.Length >= 3 ? string.Join(':', fingerprint[..^2]) : "unknown-strategy";
        var version = fingerprint.Length >= 2 ? fingerprint[^2] : "unknown";
        return new(value.Id,
        strategyId, version, value.InstrumentId, value.Direction,
        SignalEntryType.Market, value.ProposedEntry, value.ProposedStopLoss, value.ProposedTarget,
        Math.Abs(value.ProposedTarget - value.ProposedEntry) /
        Math.Abs(value.ProposedEntry - value.ProposedStopLoss), value.Confidence,
        MarketRegime.Uncertain, [], [], value.MarketDataTimestampUtc, value.ExpiresAtUtc);
    }

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
        if (await db.StrategyEvaluations.AnyAsync(value =>
                value.StrategyCode == strategy.StrategyId &&
                value.InstrumentId == instrumentId &&
                value.CandleTimeUtc == candleTimeUtc, cancellationToken))
            return;

        var recordedStrategyId = signal?.StrategyId ?? strategy.StrategyId;
        var recordedStrategyVersion = signal?.StrategyVersion ?? strategy.Version;
        db.StrategyEvaluations.Add(new StrategyEvaluation(Guid.NewGuid(), recordedStrategyId,
            recordedStrategyVersion, instrumentId, candleTimeUtc, currentPrice, openingRangeHigh,
            openingRangeLow, vwap, fastEma, slowEma, atrPercent, relativeFuturesVolume,
            regime.Regime, regime.DirectionalBias, regime.Confidence, outcome,
            JsonSerializer.Serialize(failedConditions), signal?.SignalId,
            option?.TradingSymbol, option?.Type.ToString(), option?.ExpiryDate,
            option?.StrikePrice, optionPremium, timeProvider.GetUtcNow(),
            shadowStructure?.State.ToString(), shadowStructure?.TrendQuality,
            shadowStructure?.WouldPermit,
            shadowStructure is null ? null : JsonSerializer.Serialize(shadowStructure.Reasons)));
        await db.SaveChangesAsync(cancellationToken);
    }

    private static void RecordPortfolioRisk(PaperAutomationState automationState,
        RebuiltTradeState tradeState, decimal maximumDailyLoss, bool reconciliationHealthy)
    {
        var marks = automationState.GetCurrent().ActivePositionMarks ?? [];
        var unrealised = marks.Sum(mark => mark.UnrealisedPnl.GetValueOrDefault());
        automationState.RecordPortfolioRisk(
            tradeState.OpenPositions.Count,
            tradeState.OpenPositions.Sum(open =>
                open.Entry.AverageFillPrice.GetValueOrDefault() * open.RemainingQuantity),
            tradeState.OpenPositions.Sum(open =>
                Math.Abs(open.Entry.AverageFillPrice.GetValueOrDefault() - open.StopLoss) *
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

    private static decimal AtrPercent(Candle[] values)
    {
        var ranges = new List<decimal>();
        for (var index = 1; index < values.Length; index++)
            ranges.Add(Math.Max(values[index].High - values[index].Low,
                Math.Max(Math.Abs(values[index].High - values[index - 1].Close),
                    Math.Abs(values[index].Low - values[index - 1].Close))));
        return ranges.Average() / values[^1].Close * 100m;
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
        decimal StopLoss, decimal Target, int RemainingQuantity, BrokerOrderSnapshot? PartialExit);
    private sealed record RebuiltTradeState(int TradesToday, decimal RealisedPnl,
        IReadOnlyList<OpenTrade> OpenPositions);

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
