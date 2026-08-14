using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Strategies;

public sealed class OpeningRangeBreakoutOptions
{
    public decimal BreakoutBufferPercent { get; init; } = 0.05m;
    public decimal MinimumRelativeVolume { get; init; } = 0.75m;
    public decimal MinimumRegimeConfidence { get; init; } = 0.50m;
    public decimal RewardToRiskRatio { get; init; } = 2m;
    public int SignalExpirySeconds { get; init; } = 30;
    public int CooldownMinutes { get; init; } = 30;
    public int MaximumTradesPerDay { get; init; } = 1;
    public bool EnforceDailyTradeLimit { get; init; } = true;
}

public sealed class OpeningRangeBreakoutStrategy(OpeningRangeBreakoutOptions options) : ITradingStrategy
{
    public string StrategyId => "opening-range-breakout";
    public string Version => "1.0.0";

    public StrategySignal? Evaluate(StrategyEvaluationContext context) => EvaluateDetailed(context).Signal;

    public StrategyEvaluationResult EvaluateDetailed(StrategyEvaluationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var failed = new List<string>();
        if (!context.RegimeTradingPermitted) failed.Add("Market regime does not permit trading.");
        if (!context.DataTradingPermitted) failed.Add("Market data does not permit trading.");
        if (context.RegimeConfidence < options.MinimumRegimeConfidence)
            failed.Add(FormattableString.Invariant(
                $"Regime confidence {context.RegimeConfidence * 100m:F0}% is below {options.MinimumRegimeConfidence * 100m:F0}%."));
        if (options.EnforceDailyTradeLimit && context.TradesToday >= options.MaximumTradesPerDay)
            failed.Add("Strategy daily trade limit reached.");
        if (context.LastSignalAtUtc is not null &&
            context.ObservedAtUtc - context.LastSignalAtUtc < TimeSpan.FromMinutes(options.CooldownMinutes))
            failed.Add($"Strategy cooldown of {options.CooldownMinutes} minutes is active.");
        if (failed.Count > 0) return new(null, failed);
        if (context.OpeningRangeHigh <= context.OpeningRangeLow || context.CurrentPrice <= 0)
            throw new ArgumentException("Opening-range context is invalid.", nameof(context));

        var buffer = context.CurrentPrice * options.BreakoutBufferPercent / 100m;
        var bullish = context.CurrentPrice > context.OpeningRangeHigh + buffer &&
            context.RegimeBias == Direction.Buy && context.Regime is
                MarketRegime.StrongBullishTrend or MarketRegime.WeakBullishTrend or
                MarketRegime.GapUpContinuation or MarketRegime.GapDownReversal or
                MarketRegime.HighVolatilityExpansion;
        var bearish = context.CurrentPrice < context.OpeningRangeLow - buffer &&
            context.RegimeBias == Direction.Sell && context.Regime is
                MarketRegime.StrongBearishTrend or MarketRegime.WeakBearishTrend or
                MarketRegime.GapDownContinuation or MarketRegime.GapUpRejection or
                MarketRegime.HighVolatilityExpansion;
        if (!bullish && !bearish)
            return new(null,
                ["Price and regime direction do not form a confirmed opening-range breakout."]);

        var direction = bullish ? Direction.Buy : Direction.Sell;
        var entry = context.CurrentPrice;
        var stop = bullish ? context.OpeningRangeLow : context.OpeningRangeHigh;
        var risk = Math.Abs(entry - stop);
        if (risk <= 0) return new(null, ["The proposed stop does not define positive risk."]);
        var target = bullish ? entry + risk * options.RewardToRiskRatio : entry - risk * options.RewardToRiskRatio;
        var confidence = Math.Clamp(0.50m + context.RegimeConfidence * 0.30m +
            Math.Clamp(context.RelativeVolume, 0m, 1m) * 0.20m, 0m, 1m);
        var signal = new StrategySignal(Guid.NewGuid(), StrategyId, Version, context.InstrumentId,
            direction, SignalEntryType.Market, entry, stop, target, options.RewardToRiskRatio,
            confidence,
            context.Regime,
            ["Opening range broken with buffer.",
             context.RelativeVolume >= options.MinimumRelativeVolume
                 ? "Futures volume confirms direction."
                 : "Price pattern qualified without volume confirmation."],
            ["Price returns inside opening range.", "Market-data or regime permission is withdrawn."],
            context.ObservedAtUtc.ToUniversalTime(),
            context.ObservedAtUtc.AddSeconds(options.SignalExpirySeconds).ToUniversalTime());
        return new(signal, []);
    }
}
