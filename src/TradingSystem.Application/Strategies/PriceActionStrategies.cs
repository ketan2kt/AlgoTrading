using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Strategies;

public sealed class PriceActionStrategyOptions
{
    public decimal MinimumScore { get; init; } = 0.55m;
    public decimal RewardToRiskRatio { get; init; } = 2m;
    public int SignalExpirySeconds { get; init; } = 30;
    public int CooldownMinutes { get; init; } = 20;
    public int MaximumTradesPerDay { get; init; } = 3;
}

public abstract class PriceActionStrategyBase(PriceActionStrategyOptions options) : ITradingStrategy
{
    protected PriceActionStrategyOptions Options { get; } = options;
    public abstract string StrategyId { get; }
    public string Version => "1.0.0";

    public StrategySignal? Evaluate(StrategyEvaluationContext context) => EvaluateDetailed(context).Signal;
    public abstract StrategyEvaluationResult EvaluateDetailed(StrategyEvaluationContext context);

    protected List<string> SafetyFailures(StrategyEvaluationContext context)
    {
        var failures = new List<string>();
        if (!context.DataTradingPermitted) failures.Add("Market data does not permit trading.");
        if (context.TradesToday >= Options.MaximumTradesPerDay)
            failures.Add("Paper daily trade limit reached.");
        if (context.LastSignalAtUtc is not null &&
            context.ObservedAtUtc - context.LastSignalAtUtc < TimeSpan.FromMinutes(Options.CooldownMinutes))
            failures.Add($"Shared cooldown of {Options.CooldownMinutes} minutes is active.");
        if (context.RecentCandles.Count < 4 || context.Vwap <= 0 ||
            context.FastEma <= 0 || context.SlowEma <= 0 || context.AtrPercent <= 0)
            failures.Add("Price-action indicators or recent candles are incomplete.");
        return failures;
    }

    protected static decimal Score(StrategyEvaluationContext context, bool trendAligned, bool structureConfirmed)
    {
        var pattern = 0.35m;
        var trend = trendAligned ? 0.25m : 0m;
        var structure = structureConfirmed ? 0.20m : 0m;
        var regime = context.RegimeBias is null ? 0.05m : 0.10m * context.RegimeConfidence;
        var volume = 0.10m * Math.Clamp(context.RelativeVolume, 0m, 1m);
        return Math.Clamp(pattern + trend + structure + regime + volume, 0m, 1m);
    }

    protected StrategyEvaluationResult Signal(StrategyEvaluationContext context, Direction direction,
        decimal stop, decimal score, params string[] reasons)
    {
        if (score < Options.MinimumScore)
            return new(null, [$"Setup score {score:P0} is below {Options.MinimumScore:P0}."]);
        var risk = Math.Abs(context.CurrentPrice - stop);
        if (risk <= 0) return new(null, ["The chart pattern does not define positive risk."]);
        var target = direction == Direction.Buy
            ? context.CurrentPrice + risk * Options.RewardToRiskRatio
            : context.CurrentPrice - risk * Options.RewardToRiskRatio;
        return new(new StrategySignal(Guid.NewGuid(), StrategyId, Version, context.InstrumentId,
            direction, SignalEntryType.Market, context.CurrentPrice, stop, target,
            Options.RewardToRiskRatio, score, context.Regime, reasons,
            ["Confirmation candle fails or price crosses the structural stop."],
            context.ObservedAtUtc.ToUniversalTime(),
            context.ObservedAtUtc.AddSeconds(Options.SignalExpirySeconds).ToUniversalTime()), []);
    }
}

public sealed class OpeningRangeRetestStrategy(PriceActionStrategyOptions options)
    : PriceActionStrategyBase(options)
{
    public override string StrategyId => "opening-range-retest";

    public override StrategyEvaluationResult EvaluateDetailed(StrategyEvaluationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var failures = SafetyFailures(context);
        if (failures.Count > 0) return new(null, failures);

        var bars = context.RecentCandles;
        var confirmation = bars[^1];
        var retest = bars[^2];
        var earlier = bars.Take(bars.Count - 2).TakeLast(4).ToArray();
        var tolerance = context.CurrentPrice * context.AtrPercent / 100m * 0.30m;
        var bullishPattern = earlier.Any(value => value.High > context.OpeningRangeHigh) &&
            retest.Low <= context.OpeningRangeHigh + tolerance && retest.Close >= context.OpeningRangeHigh &&
            confirmation.Close > retest.High && confirmation.Close > context.OpeningRangeHigh;
        var bearishPattern = earlier.Any(value => value.Low < context.OpeningRangeLow) &&
            retest.High >= context.OpeningRangeLow - tolerance && retest.Close <= context.OpeningRangeLow &&
            confirmation.Close < retest.Low && confirmation.Close < context.OpeningRangeLow;
        if (!bullishPattern && !bearishPattern)
            return new(null, ["No confirmed opening-range breakout, retest and continuation sequence."]);

        var direction = bullishPattern ? Direction.Buy : Direction.Sell;
        var trendAligned = direction == Direction.Buy
            ? context.CurrentPrice > context.Vwap && context.FastEma > context.SlowEma
            : context.CurrentPrice < context.Vwap && context.FastEma < context.SlowEma;
        var structureConfirmed = direction == Direction.Buy
            ? confirmation.Close > confirmation.Open
            : confirmation.Close < confirmation.Open;
        if (!trendAligned) return new(null, ["Retest formed but VWAP and EMA trend are not aligned."]);
        if (context.RegimeBias is not null && context.RegimeBias != direction)
            return new(null, ["Retest direction conflicts with the current market bias."]);
        var stop = direction == Direction.Buy
            ? Math.Min(retest.Low, context.OpeningRangeHigh - tolerance)
            : Math.Max(retest.High, context.OpeningRangeLow + tolerance);
        return Signal(context, direction, stop, Score(context, trendAligned, structureConfirmed),
            "Opening-range breakout was retested and held.",
            "Confirmation candle, VWAP and EMA structure align.");
    }
}

public sealed class VwapTrendPullbackStrategy(PriceActionStrategyOptions options)
    : PriceActionStrategyBase(options)
{
    public override string StrategyId => "vwap-trend-pullback";

    public override StrategyEvaluationResult EvaluateDetailed(StrategyEvaluationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var failures = SafetyFailures(context);
        if (failures.Count > 0) return new(null, failures);

        var confirmation = context.RecentCandles[^1];
        var pullback = context.RecentCandles[^2];
        var tolerance = context.CurrentPrice * context.AtrPercent / 100m * 0.35m;
        var bullishTrend = context.FastEma > context.SlowEma && context.CurrentPrice > context.Vwap;
        var bearishTrend = context.FastEma < context.SlowEma && context.CurrentPrice < context.Vwap;
        var bullishPattern = bullishTrend && pullback.Low <= context.Vwap + tolerance &&
            pullback.Close >= context.Vwap - tolerance && confirmation.Close > pullback.High &&
            confirmation.Close > confirmation.Open;
        var bearishPattern = bearishTrend && pullback.High >= context.Vwap - tolerance &&
            pullback.Close <= context.Vwap + tolerance && confirmation.Close < pullback.Low &&
            confirmation.Close < confirmation.Open;
        if (!bullishPattern && !bearishPattern)
            return new(null, ["No confirmed VWAP pullback and trend-continuation candle."]);

        var direction = bullishPattern ? Direction.Buy : Direction.Sell;
        if (context.RegimeBias is not null && context.RegimeBias != direction)
            return new(null, ["VWAP continuation direction conflicts with the current market bias."]);
        var stop = direction == Direction.Buy
            ? Math.Min(pullback.Low, context.Vwap - tolerance)
            : Math.Max(pullback.High, context.Vwap + tolerance);
        return Signal(context, direction, stop, Score(context, true, true),
            "Price pulled back to VWAP within an established EMA trend.",
            "A directional confirmation candle resumed the trend.");
    }
}

public sealed class CompositeTradingStrategy(IReadOnlyList<ITradingStrategy> strategies) : ITradingStrategy
{
    public string StrategyId => "price-action-portfolio";
    public string Version => "1.0.0";
    public StrategySignal? Evaluate(StrategyEvaluationContext context) => EvaluateDetailed(context).Signal;

    public StrategyEvaluationResult EvaluateDetailed(StrategyEvaluationContext context)
    {
        var results = strategies.Select(strategy => (strategy, result: strategy.EvaluateDetailed(context))).ToArray();
        var selected = results.Where(value => value.result.Signal is not null)
            .Select(value => value.result.Signal!)
            .OrderByDescending(value => value.Confidence)
            .ThenBy(value => value.StrategyId, StringComparer.Ordinal)
            .FirstOrDefault();
        if (selected is not null) return new(selected, []);
        return new(null, results.SelectMany(value => value.result.FailedConditions
            .Select(reason => $"{value.strategy.StrategyId}: {reason}")).ToArray());
    }
}
