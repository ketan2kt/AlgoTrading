using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Strategies;

public sealed class PriceActionStrategyOptions
{
    public decimal MinimumScore { get; init; } = 0.55m;
    public decimal RewardToRiskRatio { get; init; } = 2m;
    public int SignalExpirySeconds { get; init; } = 30;
    public int CooldownMinutes { get; init; } = 20;
    public int MaximumTradesPerDay { get; init; } = 3;
    public int MaximumTradesPerStrategyPerDay { get; init; } = 2;
    public bool EnforceDailyTradeLimits { get; init; } = true;
}

public abstract class PriceActionStrategyBase(PriceActionStrategyOptions options) : ITradingStrategy
{
    protected PriceActionStrategyOptions Options { get; } = options;
    public abstract string StrategyId { get; }
    public string Version => "1.0.0";

    public StrategySignal? Evaluate(StrategyEvaluationContext context) => EvaluateDetailed(context).Signal;
    public abstract StrategyEvaluationResult EvaluateDetailed(StrategyEvaluationContext context);

    protected List<string> SafetyFailures(StrategyEvaluationContext context,
        bool permitRegimeOverride = false)
    {
        var failures = new List<string>();
        if (!context.DataTradingPermitted) failures.Add("Market data does not permit trading.");
        if (!context.RegimeTradingPermitted && !permitRegimeOverride)
            failures.Add("Market regime does not permit directional trading.");
        if (Options.EnforceDailyTradeLimits && context.TradesToday >= Options.MaximumTradesPerDay)
            failures.Add("Paper daily trade limit reached.");
        if (Options.EnforceDailyTradeLimits && context.TradesByStrategyToday.GetValueOrDefault(StrategyId) >=
            Options.MaximumTradesPerStrategyPerDay)
            failures.Add($"Daily allocation for {StrategyId} is exhausted.");
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

public sealed class EmaPullbackContinuationStrategy(PriceActionStrategyOptions options)
    : PriceActionStrategyBase(options)
{
    public override string StrategyId => "ema-pullback-continuation";

    public override StrategyEvaluationResult EvaluateDetailed(StrategyEvaluationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var failures = SafetyFailures(context);
        if (failures.Count > 0) return new(null, failures);
        var pullback = context.RecentCandles[^2];
        var confirmation = context.RecentCandles[^1];
        var tolerance = context.CurrentPrice * context.AtrPercent / 100m * 0.25m;
        var bullish = context.FastEma > context.SlowEma &&
            pullback.Low <= context.FastEma + tolerance && pullback.Close >= context.SlowEma &&
            confirmation.Close > pullback.High && confirmation.Close > confirmation.Open;
        var bearish = context.FastEma < context.SlowEma &&
            pullback.High >= context.FastEma - tolerance && pullback.Close <= context.SlowEma &&
            confirmation.Close < pullback.Low && confirmation.Close < confirmation.Open;
        if (!bullish && !bearish)
            return new(null, ["No EMA 9/21 pullback with a confirmed continuation candle."]);
        var direction = bullish ? Direction.Buy : Direction.Sell;
        if (context.RegimeBias != direction)
            return new(null, ["EMA continuation direction lacks matching regime bias."]);
        var structureAligned = direction == Direction.Buy
            ? context.MarketStructure.Direction == MarketStructureDirection.Bullish
            : context.MarketStructure.Direction == MarketStructureDirection.Bearish;
        var stop = direction == Direction.Buy ? pullback.Low - tolerance : pullback.High + tolerance;
        return Signal(context, direction, stop, Score(context, true, structureAligned),
            "Price retraced into the EMA 9/21 trend zone.",
            "The confirmation candle resumed the prevailing trend.");
    }
}

public sealed class RangeBreakoutRetestStrategy(PriceActionStrategyOptions options)
    : PriceActionStrategyBase(options)
{
    public override string StrategyId => "intraday-range-breakout-retest";

    public override StrategyEvaluationResult EvaluateDetailed(StrategyEvaluationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var failures = SafetyFailures(context);
        if (failures.Count > 0) return new(null, failures);
        var bars = context.RecentCandles;
        if (bars.Count < 8) return new(null, ["At least eight candles are required to define an intraday range."]);
        var rangeBars = bars.TakeLast(8).Take(6).ToArray();
        var retest = bars[^2];
        var confirmation = bars[^1];
        var rangeHigh = rangeBars.Max(x => x.High);
        var rangeLow = rangeBars.Min(x => x.Low);
        var tolerance = context.CurrentPrice * context.AtrPercent / 100m * 0.25m;
        var bullish = retest.Low <= rangeHigh + tolerance && retest.Close >= rangeHigh &&
            confirmation.Close > retest.High;
        var bearish = retest.High >= rangeLow - tolerance && retest.Close <= rangeLow &&
            confirmation.Close < retest.Low;
        if (!bullish && !bearish)
            return new(null, ["No intraday consolidation breakout, retest and continuation sequence."]);
        var direction = bullish ? Direction.Buy : Direction.Sell;
        if (context.RegimeBias != direction)
            return new(null, ["Range breakout direction lacks matching regime bias."]);
        var trendAligned = direction == Direction.Buy
            ? context.CurrentPrice > context.Vwap : context.CurrentPrice < context.Vwap;
        if (!trendAligned) return new(null, ["Range breakout is not aligned with VWAP."]);
        var stop = direction == Direction.Buy ? retest.Low - tolerance : retest.High + tolerance;
        return Signal(context, direction, stop, Score(context, true, true),
            "A six-candle intraday range broke and held on retest.", "VWAP confirms breakout direction.");
    }
}

public sealed class VwapRejectionReversalStrategy(PriceActionStrategyOptions options)
    : PriceActionStrategyBase(options)
{
    public override string StrategyId => "vwap-rejection-reversal";

    public override StrategyEvaluationResult EvaluateDetailed(StrategyEvaluationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var failures = SafetyFailures(context);
        if (failures.Count > 0) return new(null, failures);
        var bars = context.RecentCandles;
        var test = bars[^2];
        var confirmation = bars[^1];
        var tolerance = context.CurrentPrice * context.AtrPercent / 100m * 0.30m;
        var bullish = test.Low <= context.Vwap + tolerance && test.Close > context.Vwap &&
            confirmation.Close > test.High && confirmation.Close > confirmation.Open;
        var bearish = test.High >= context.Vwap - tolerance && test.Close < context.Vwap &&
            confirmation.Close < test.Low && confirmation.Close < confirmation.Open;
        if (!bullish && !bearish)
            return new(null, ["VWAP was not rejected with a reversal confirmation candle."]);
        var direction = bullish ? Direction.Buy : Direction.Sell;
        var structureConfirmed = direction == Direction.Buy
            ? context.MarketStructure.Direction == MarketStructureDirection.Bullish
            : context.MarketStructure.Direction == MarketStructureDirection.Bearish;
        if (!structureConfirmed)
            return new(null, ["VWAP reversal lacks confirmed directional market structure."]);
        if (context.RegimeBias != direction)
            return new(null, ["VWAP reversal direction conflicts with the current market bias."]);
        var stop = direction == Direction.Buy ? test.Low - tolerance : test.High + tolerance;
        return Signal(context, direction, stop, Score(context, false, structureConfirmed),
            "Price rejected VWAP and closed back on the directional side.",
            "The next candle confirmed the reversal.");
    }
}

public sealed class MomentumExpansionStrategy(PriceActionStrategyOptions options)
    : PriceActionStrategyBase(options)
{
    public override string StrategyId => "momentum-expansion";

    public override StrategyEvaluationResult EvaluateDetailed(StrategyEvaluationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var failures = SafetyFailures(context, permitRegimeOverride: true);
        if (failures.Count > 0) return new(null, failures);
        var bars = context.RecentCandles;
        var confirmation = bars[^1];
        var prior = bars.TakeLast(Math.Min(7, bars.Count)).SkipLast(1).ToArray();
        var averageRange = prior.Average(x => x.High - x.Low);
        var currentRange = confirmation.High - confirmation.Low;
        if (averageRange <= 0 || currentRange < averageRange * 1.45m)
            return new(null, ["Latest candle is not a 1.45x range expansion."]);
        var bullish = confirmation.Close > confirmation.Open && confirmation.Close > prior.Max(x => x.High) &&
            confirmation.Close > context.Vwap;
        var bearish = confirmation.Close < confirmation.Open && confirmation.Close < prior.Min(x => x.Low) &&
            confirmation.Close < context.Vwap;
        if (!bullish && !bearish)
            return new(null, ["Expansion candle did not close beyond recent structure and VWAP."]);
        var direction = bullish ? Direction.Buy : Direction.Sell;
        var structureAligned = direction == Direction.Buy
            ? context.MarketStructure.Direction == MarketStructureDirection.Bullish
            : context.MarketStructure.Direction == MarketStructureDirection.Bearish;
        var trendAligned = direction == Direction.Buy
            ? context.FastEma > context.SlowEma && context.CurrentPrice > context.Vwap
            : context.FastEma < context.SlowEma && context.CurrentPrice < context.Vwap;
        if (!trendAligned)
            return new(null, ["Momentum expansion is not aligned with EMA 9/21 and VWAP."]);
        var regimeAligned = context.RegimeBias is null || context.RegimeBias == direction;
        var stop = direction == Direction.Buy ? confirmation.Low : confirmation.High;
        var score = Score(context, true, structureAligned) - (regimeAligned ? 0m : 0.10m);
        return Signal(context, direction, stop, score,
            "Candle range expanded beyond its recent baseline.",
            "Close broke recent structure on the directional side of VWAP.",
            regimeAligned ? "Regime direction supports the move." :
                "Price expansion overrode lagging regime direction with a 10% confidence penalty.");
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
        var signals = results.Where(value => value.result.Signal is not null)
            .Select(value => value.result.Signal!).ToArray();
        var directionalGroups = signals.GroupBy(value => value.Direction).ToArray();
        if (directionalGroups.Length > 1)
            return new(null, ["Price-action strategies disagree on direction; no trade is taken."]);
        var consensus = directionalGroups.SingleOrDefault();
        if (consensus is not null && consensus.Count() >= 2)
        {
            var selected = consensus.OrderByDescending(value => value.Confidence)
                .ThenBy(value => value.StrategyId, StringComparer.Ordinal).First();
            var members = string.Join(", ", consensus.Select(value => value.StrategyId)
                .OrderBy(value => value, StringComparer.Ordinal));
            return new(selected with
            {
                SupportingReasons = selected.SupportingReasons
                    .Append($"Directional consensus from: {members}.").ToArray()
            }, []);
        }
        if (signals.Length == 1)
        {
            var single = signals[0];
            var structureAligned = single.Direction == Direction.Buy
                ? context.MarketStructure.Direction == MarketStructureDirection.Bullish
                : context.MarketStructure.Direction == MarketStructureDirection.Bearish;
            var highQualityMomentum = single.StrategyId == "momentum-expansion" &&
                                      single.Confidence >= 0.70m &&
                                      context.RegimeBias == single.Direction &&
                                      structureAligned;
            if (highQualityMomentum)
                return new(single with
                {
                    SupportingReasons = single.SupportingReasons
                        .Append("High-quality momentum expansion passed the strong single-signal exception.")
                        .ToArray()
                }, []);
            var explorationEligible = IsExplorationEligible(single, context);
            if (explorationEligible)
                return new(single with
                {
                    StrategyId = $"exploration-{single.StrategyId}",
                    SupportingReasons = single.SupportingReasons
                        .Append("Exploration lane: one strong strategy qualified with EMA/VWAP and structure alignment, and no opposing signal.")
                        .ToArray()
                }, []);
            return new(null, [$"Only {single.StrategyId} qualified; two-strategy consensus or a high-quality momentum expansion is required."]);
        }
        return new(null, results.SelectMany(value => value.result.FailedConditions
            .Select(reason => $"{value.strategy.StrategyId}: {reason}")).ToArray());
    }

    private static bool IsExplorationEligible(StrategySignal signal,
        StrategyEvaluationContext context)
    {
        if (signal.Confidence < 0.62m || !KnownExplorationStrategy(signal.StrategyId)) return false;
        var emaVwapAligned = signal.Direction == Direction.Buy
            ? context.FastEma > context.SlowEma && context.CurrentPrice > context.Vwap
            : context.FastEma < context.SlowEma && context.CurrentPrice < context.Vwap;
        var structureAligned = signal.Direction == Direction.Buy
            ? context.MarketStructure.Direction == MarketStructureDirection.Bullish
            : context.MarketStructure.Direction == MarketStructureDirection.Bearish;
        return emaVwapAligned && structureAligned;
    }

    private static bool KnownExplorationStrategy(string strategyId) => strategyId is
        "opening-range-breakout" or "opening-range-retest" or "vwap-trend-pullback" or
        "ema-pullback-continuation" or "intraday-range-breakout-retest" or
        "vwap-rejection-reversal" or "momentum-expansion";
}
