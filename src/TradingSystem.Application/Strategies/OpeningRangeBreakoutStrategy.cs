using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Strategies;

public sealed class OpeningRangeBreakoutOptions
{
    public decimal BreakoutBufferPercent { get; init; } = 0.05m;
    public decimal MinimumRelativeVolume { get; init; } = 1.25m;
    public decimal MinimumRegimeConfidence { get; init; } = 0.65m;
    public decimal RewardToRiskRatio { get; init; } = 2m;
    public int SignalExpirySeconds { get; init; } = 30;
    public int CooldownMinutes { get; init; } = 30;
    public int MaximumTradesPerDay { get; init; } = 1;
}

public sealed class OpeningRangeBreakoutStrategy(OpeningRangeBreakoutOptions options) : ITradingStrategy
{
    public string StrategyId => "opening-range-breakout";
    public string Version => "1.0.0";

    public StrategySignal? Evaluate(StrategyEvaluationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.RegimeTradingPermitted || !context.DataTradingPermitted ||
            context.RegimeConfidence < options.MinimumRegimeConfidence ||
            context.RelativeVolume < options.MinimumRelativeVolume ||
            context.TradesToday >= options.MaximumTradesPerDay ||
            context.LastSignalAtUtc is not null &&
            context.ObservedAtUtc - context.LastSignalAtUtc < TimeSpan.FromMinutes(options.CooldownMinutes))
            return null;
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
        if (!bullish && !bearish) return null;

        var direction = bullish ? Direction.Buy : Direction.Sell;
        var entry = context.CurrentPrice;
        var stop = bullish ? context.OpeningRangeLow : context.OpeningRangeHigh;
        var risk = Math.Abs(entry - stop);
        if (risk <= 0) return null;
        var target = bullish ? entry + risk * options.RewardToRiskRatio : entry - risk * options.RewardToRiskRatio;
        return new StrategySignal(Guid.NewGuid(), StrategyId, Version, context.InstrumentId,
            direction, SignalEntryType.Market, entry, stop, target, options.RewardToRiskRatio,
            Math.Clamp((context.RegimeConfidence + Math.Min(context.RelativeVolume / 3m, 1m)) / 2m, 0m, 1m),
            context.Regime,
            ["Opening range broken with buffer.", "Relative volume and regime bias confirm direction."],
            ["Price returns inside opening range.", "Market-data or regime permission is withdrawn."],
            context.ObservedAtUtc.ToUniversalTime(),
            context.ObservedAtUtc.AddSeconds(options.SignalExpirySeconds).ToUniversalTime());
    }
}
