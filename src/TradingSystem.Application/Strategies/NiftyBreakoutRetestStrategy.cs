using TradingSystem.Application.Execution;
using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Strategies;

// Reconstruct the setup from completed candles, so the same opportunity
// survives a restart and is available in historical replay without saved signals.
public sealed class NiftyBreakoutRetestStrategy(PriceActionStrategyOptions options)
    : PriceActionStrategyBase(options)
{
    public const string Id = "nifty-completed-breakout-retest";
    public override string StrategyId => Id;

    public static bool AllowsCurrentPrice(StrategySignal signal, decimal price)
    {
        if (signal.RetestChaseBoundary is not { } boundary) return true;
        if (signal.IndexConfirmationLevel is not { } pivot || price <= 0m) return false;
        return signal.Direction == Direction.Buy
            ? price > pivot && price <= boundary
            : price < pivot && price >= boundary;
    }

    public override StrategyEvaluationResult EvaluateDetailed(StrategyEvaluationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.RelaxedNiftyEntries) return new(null, ["The completed retest route is Nifty-only."]);
        var failures = SafetyFailures(context);
        if (failures.Count > 0) return new(null, failures);
        var interval = context.CandleIntervalSeconds;
        if (interval is not (60 or 300))
            return new(null, ["A completed one- or five-minute retest is required."]);
        var bars = context.RecentCandles.TakeLast(15).ToArray();
        if (bars.Length < 15 || bars[^1].OpenTimeUtc.AddSeconds(interval) != context.ObservedAtUtc ||
            bars.Zip(bars.Skip(1)).Any(p => p.Second.OpenTimeUtc - p.First.OpenTimeUtc != TimeSpan.FromSeconds(interval)))
            return new(null, ["A contiguous, completed breakout and retest window is required."]);

        var last = bars[^1];
        var direction = last.Close > last.Open ? Direction.Buy : Direction.Sell;
        var sign = direction == Direction.Buy ? 1m : -1m;
        var atr = context.CurrentPrice * context.AtrPercent / 100m;
        if (atr <= 0m || last.Close != context.CurrentPrice || sign * (last.Close - last.Open) <= 0m ||
            !NiftyEntryTrialProfile.TechnicalDirection(context, direction) ||
            !FirstEntryTimingPolicy.Evaluate(context.RecentCandles, direction).Permitted)
            return new(null, ["A fresh EMA/VWAP-aligned confirmation within the existing chase limits is required."]);

        // The range is frozen before the impulse. A later entry needs at
        // least two completed pullback bars and a new confirmation bar.
        for (var back = 3; back <= 6; back++)
        {
            var impulseIndex = bars.Length - 1 - back;
            var impulse = bars[impulseIndex];
            var baseBars = bars.Skip(impulseIndex - 8).Take(8);
            var level = direction == Direction.Buy ? baseBars.Max(b => b.High) : baseBars.Min(b => b.Low);
            var breakDistance = sign * (impulse.Close - level);
            if (breakDistance < .10m * atr || sign * (impulse.Close - impulse.Open) <= 0m ||
                sign * (last.Close - level) > FirstEntryTimingPolicy.MaximumTriggerExtensionAtr * atr)
                continue;

            var pullback = bars[(impulseIndex + 1)..^1];
            if (pullback.Any(b => sign * (b.Close - level) < 0m) ||
                !pullback.Any(b => sign * (b.Close - b.Open) < 0m &&
                    sign * (impulse.Close - b.Close) >= .25m * atr))
                continue;
            decimal NearEdge(StrategyPriceBar b) => direction == Direction.Buy ? b.Low : b.High;
            if (pullback.Any(b => sign * (NearEdge(b) - level) < -.25m * atr))
                continue;
            var touch = Array.FindIndex(pullback, b => sign * (NearEdge(b) - level) <= .25m * atr);
            if (touch < 0) continue;
            var retest = pullback[touch..];
            var pivot = direction == Direction.Buy ? retest.Max(b => b.High) : retest.Min(b => b.Low);
            if (sign * (last.Close - pivot) <= 0m) continue;
            var stop = direction == Direction.Buy
                ? Math.Min(level, retest.Min(b => b.Low)) - .10m * atr
                : Math.Max(level, retest.Max(b => b.High)) + .10m * atr;
            if (stop <= 0m) continue;
            var result = Signal(context, direction, stop, Score(context, true, true),
                "The original breakout level held through a completed multi-candle pullback.",
                "A new directional candle cleared the retest pivot without chasing the impulse.");
            if (result.Signal is { } signal)
                return result with { Signal = signal with { IndexConfirmationLevel = pivot,
                    RetestChaseBoundary = level + sign * FirstEntryTimingPolicy.MaximumTriggerExtensionAtr * atr } };
        }
        return new(null, ["No completed multi-candle retest held the original breakout level."]);
    }
}
