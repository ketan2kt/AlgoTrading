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

        var now = timeProvider.GetUtcNow();
        var indiaNow = TimeZoneInfo.ConvertTime(now, IndiaTimeZone);
        if (indiaNow.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            state.Record("MarketClosed", false, "Waiting for the next NSE weekday session.");
            return;
        }

        var sessionStart = ToUtc(indiaNow.Date, new TimeOnly(9, 15));
        var sessionEnd = ToUtc(indiaNow.Date, new TimeOnly(15, 30));
        var signals = await LoadSessionSignalsAsync(db, instrument.Id, sessionStart, sessionEnd, cancellationToken);
        var killSwitch = await db.ApplicationSettings.AsNoTracking().Where(value =>
                value.Mode == TradingMode.Paper && value.Key == "EmergencyKillSwitch")
            .Select(value => value.ValueJson).SingleOrDefaultAsync(cancellationToken);
        var killSwitchActive = bool.TryParse(killSwitch, out var active) && active;
        var tradeState = await RebuildTradeStateAsync(signals, cancellationToken);
        var reconciliation = await broker.ReconcileAsync(
            tradeState.Open is null ? [] : [new ExpectedBrokerPosition(instrument.Id,
                tradeState.Open.Signal.Direction, tradeState.Open.Entry.FilledQuantity)], cancellationToken);
        if (!reconciliation.TradingPermitted)
        {
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

        if (tradeState.Open is not null)
        {
            if (latest is null)
            {
                state.Record("PositionUnmonitored", false,
                    "An open paper position exists but no market price is available.",
                    tradeState.TradesToday, tradeState.RealisedPnl, 0m,
                    tradeState.Open.Signal.SignalId, tradeState.Open.Signal.Direction.ToString(),
                    tradeState.Open.Entry.FilledQuantity, tradeState.Open.Entry.AverageFillPrice,
                    tradeState.Open.Signal.ProposedStopLoss, tradeState.Open.Signal.ProposedTarget);
                return;
            }

            await ManageOpenPositionAsync(scope.ServiceProvider, tradeState, latest.Price,
                indiaNow.TimeOfDay, fresh, killSwitchActive, cancellationToken);
            return;
        }

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
        var entryCutoff = ParseTime(options.Value.EntryCutoff);
        var localTime = TimeOnly.FromTimeSpan(indiaNow.TimeOfDay);
        if (localTime < openingRangeEnd || localTime >= entryCutoff)
        {
            state.Record(localTime < openingRangeEnd ? "WarmingUp" : "EntryCutoff", false,
                localTime < openingRangeEnd ? "Building the 15-minute opening range." :
                "New-entry cutoff reached; monitoring only.", tradeState.TradesToday, tradeState.RealisedPnl);
            return;
        }

        var candles = await db.Candles.AsNoTracking().Where(value =>
                value.InstrumentId == instrument.Id && value.Source == "Groww" &&
                value.IntervalSeconds == marketOptions.Value.CandleIntervalSeconds &&
                value.OpenTimeUtc >= sessionStart && value.OpenTimeUtc < sessionEnd)
            .OrderBy(value => value.OpenTimeUtc).ToListAsync(cancellationToken);
        if (candles.Count < 21)
        {
            state.Record("WarmingUp", false, $"Waiting for indicators: {candles.Count}/21 completed candles.",
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
        if (signals.Any(value => value.MarketDataTimestampUtc == candleDecisionTime))
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
        var totalVolume = candles.Sum(value => value.Volume);
        var averageVolume = candles.Take(candles.Count - 1).Average(value => (decimal)value.Volume);
        if (openingCandles.Length == 0 || totalVolume <= 0 || averageVolume <= 0)
        {
            state.Record("DataIncomplete", false,
                "Validated volume or opening-range data is unavailable; the strategy fails closed.",
                tradeState.TradesToday, tradeState.RealisedPnl);
            return;
        }

        var relativeVolume = latestCandle.Volume / averageVolume;
        var vwap = candles.Sum(value => ((value.High + value.Low + value.Close) / 3m) * value.Volume) / totalVolume;
        var closes = candles.Select(value => value.Close).ToArray();
        var fast = Ema(closes, 9);
        var slow = Ema(closes, 21);
        var atr = AtrPercent(candles.TakeLast(15).ToArray());
        var sessionId = DeterministicSessionId(DateOnly.FromDateTime(indiaNow.Date));
        var regimeService = scope.ServiceProvider.GetRequiredService<MarketRegimeService>();
        var regime = await regimeService.EvaluateAsync(new MarketRegimeInput(
            sessionId, latestCandle.OpenTimeUtc, latestCandle.Close, previousClose.Value,
            candles[0].Open, openingCandles.Max(value => value.High), openingCandles.Min(value => value.Low),
            vwap, fast, slow, atr, relativeVolume, 1m, true), cancellationToken);
        var strategy = scope.ServiceProvider.GetRequiredService<ITradingStrategy>();
        var signal = strategy.Evaluate(new StrategyEvaluationContext(instrument.Id,
            candleDecisionTime, latestCandle.Close,
            openingCandles.Max(value => value.High), openingCandles.Min(value => value.Low), relativeVolume,
            regime.Regime, regime.DirectionalBias, regime.Confidence, regime.TradingPermitted, true,
            signals.OrderByDescending(value => value.MarketDataTimestampUtc)
                .Select(value => (DateTimeOffset?)value.MarketDataTimestampUtc).FirstOrDefault(),
            tradeState.TradesToday));
        if (signal is null)
        {
            state.Record("Scanning", true,
                $"No qualifying setup. Regime: {regime.Regime} ({regime.Confidence:P0}).",
                tradeState.TradesToday, tradeState.RealisedPnl);
            return;
        }

        var audit = scope.ServiceProvider.GetRequiredService<IPaperLifecycleAuditStore>();
        await audit.PersistSignalAsync(signal, cancellationToken);
        var candidates = await db.Instruments.AsNoTracking().Where(value =>
                value.Exchange == "NSE" && value.Segment == InstrumentSegment.FuturesAndOptions &&
                (value.Type == InstrumentType.CallOption || value.Type == InstrumentType.PutOption) &&
                value.IsActive && value.ExpiryDate != null && value.StrikePrice != null)
            .Select(value => new NiftyOptionContractCandidate(value.Id, value.TradingSymbol, value.Type,
                value.ExpiryDate!.Value, value.StrikePrice!.Value, value.LotSize, value.TickSize))
            .ToListAsync(cancellationToken);
        var selectedOption = NiftyOptionContractSelector.Select(
            candidates, signal.Direction, currentPrice, DateOnly.FromDateTime(indiaNow.Date));
        if (selectedOption is null)
        {
            state.Record("OptionUniverseUnavailable", false,
                "A Nifty signal qualified, but no valid Nifty option contract was available. Index execution is blocked.",
                tradeState.TradesToday, tradeState.RealisedPnl, signalId: signal.SignalId,
                direction: signal.Direction.ToString());
            return;
        }

        state.Record("OptionSelected", false,
            $"Signal mapped to {selectedOption.TradingSymbol}. Waiting for validated option premium and spread before paper entry.",
            tradeState.TradesToday, tradeState.RealisedPnl, signalId: signal.SignalId,
            direction: signal.Direction.ToString(), optionSymbol: selectedOption.TradingSymbol,
            optionType: selectedOption.Type.ToString(), optionExpiry: selectedOption.ExpiryDate,
            optionStrike: selectedOption.StrikePrice, optionLotSize: selectedOption.LotSize);
    }

    private async Task ManageOpenPositionAsync(IServiceProvider services, RebuiltTradeState tradeState,
        decimal price, TimeSpan indiaTime, bool fresh, bool killSwitchActive,
        CancellationToken cancellationToken)
    {
        var open = tradeState.Open!;
        var multiplier = open.Signal.Direction == Direction.Buy ? 1m : -1m;
        var unrealised = (price - open.Entry.AverageFillPrice!.Value) * open.Entry.FilledQuantity * multiplier;
        var exitReason = PaperPositionExitPolicy.Evaluate(open.Signal.Direction, price,
            open.Signal.ProposedStopLoss, open.Signal.ProposedTarget,
            TimeOnly.FromTimeSpan(indiaTime), ParseTime(options.Value.ForcedExit), fresh);
        if (killSwitchActive && fresh) exitReason = PaperExitReason.EmergencyKillSwitch;
        if (exitReason == PaperExitReason.None)
        {
            state.Record(fresh ? "PositionOpen" : "PositionUnmonitored", false,
                fresh ? "Monitoring protective stop, target, and forced exit." :
                    "Market data is stale; new entries are blocked and the paper position cannot be repriced.",
                tradeState.TradesToday, tradeState.RealisedPnl, unrealised, open.Signal.SignalId,
                open.Signal.Direction.ToString(), open.Entry.FilledQuantity, open.Entry.AverageFillPrice,
                open.Signal.ProposedStopLoss, open.Signal.ProposedTarget);
            return;
        }

        var exitReference = $"{open.Signal.SignalId:N}-EXIT";
        var exitDirection = open.Signal.Direction == Direction.Buy ? Direction.Sell : Direction.Buy;
        await broker.SubmitAsync(new(exitReference, open.Signal.InstrumentId, exitDirection,
            open.Entry.FilledQuantity, price), cancellationToken);
        var exit = await paperControl.ProcessNextFillAsync(exitReference, cancellationToken);
        var grossPnl = (exit.AverageFillPrice!.Value - open.Entry.AverageFillPrice.Value) *
                       open.Entry.FilledQuantity * multiplier;
        var estimatedCosts = PaperTradingCostModel.EstimateRoundTripCost(
            open.Entry.AverageFillPrice.Value, exit.AverageFillPrice.Value,
            open.Entry.FilledQuantity, options.Value.EstimatedRoundTripCostBasisPoints);
        var pnl = grossPnl - estimatedCosts;
        var reason = exitReason.ToString();
        var writer = services.GetRequiredService<IAuditWriter>();
        await writer.WriteAsync(new AuditEntry("paper-automation", "PaperTradeClosed", "Signal",
            open.Signal.SignalId.ToString("N"), reason, "{}", JsonSerializer.Serialize(new
            {
                entryPrice = open.Entry.AverageFillPrice,
                exitPrice = exit.AverageFillPrice,
                quantity = open.Entry.FilledQuantity,
                grossPnl,
                estimatedCosts,
                realisedPnl = pnl
            }), open.Signal.SignalId.ToString("N"), timeProvider.GetUtcNow()), cancellationToken);
        state.Record("TradeClosed", false, $"Paper trade closed by {reason}; realised P&L {pnl:F2}.",
            tradeState.TradesToday, tradeState.RealisedPnl + pnl);
    }

    private async Task<RebuiltTradeState> RebuildTradeStateAsync(IReadOnlyList<Signal> signals,
        CancellationToken cancellationToken)
    {
        OpenTrade? open = null;
        var count = 0;
        var realised = 0m;
        foreach (var row in signals.OrderBy(value => value.MarketDataTimestampUtc))
        {
            var entry = await broker.GetOrderAsync($"{row.Id:N}-ENTRY", cancellationToken);
            if (entry?.State != OrderState.Filled) continue;
            count++;
            var signal = ToStrategySignal(row);
            var exit = await broker.GetOrderAsync($"{row.Id:N}-EXIT", cancellationToken);
            if (exit?.State == OrderState.Filled)
            {
                var multiplier = row.Direction == Direction.Buy ? 1m : -1m;
                var grossPnl = (exit.AverageFillPrice!.Value - entry.AverageFillPrice!.Value) *
                               entry.FilledQuantity * multiplier;
                realised += grossPnl - PaperTradingCostModel.EstimateRoundTripCost(
                    entry.AverageFillPrice.Value, exit.AverageFillPrice.Value, entry.FilledQuantity,
                    options.Value.EstimatedRoundTripCostBasisPoints);
            }
            else
            {
                open = new OpenTrade(signal, entry);
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

    private static StrategySignal ToStrategySignal(Signal value) => new(value.Id,
        "opening-range-breakout", "1.0.0", value.InstrumentId, value.Direction,
        SignalEntryType.Market, value.ProposedEntry, value.ProposedStopLoss, value.ProposedTarget,
        Math.Abs(value.ProposedTarget - value.ProposedEntry) /
        Math.Abs(value.ProposedEntry - value.ProposedStopLoss), value.Confidence,
        MarketRegime.Uncertain, [], [], value.MarketDataTimestampUtc, value.ExpiresAtUtc);

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

    private sealed record OpenTrade(StrategySignal Signal, BrokerOrderSnapshot Entry);
    private sealed record RebuiltTradeState(int TradesToday, decimal RealisedPnl, OpenTrade? Open);

    [LoggerMessage(Level = LogLevel.Error, Message = "Automated paper-trading cycle failed.")]
    private static partial void LogCycleFailed(ILogger logger, Exception exception);
}
