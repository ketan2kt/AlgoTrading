using TradingSystem.Application.MarketData;
using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Execution;

public sealed record SensexEarlyPullbackDecision(Direction? Direction, decimal Confidence,
    IReadOnlyList<string> Reasons);

/// <summary>
/// Paper-only challenger which enters an established trend on its first controlled pullback,
/// instead of waiting for price to break the impulse extreme again.
/// </summary>
public static class SensexEarlyPullbackResearchPolicy
{
    public static SensexEarlyPullbackDecision Evaluate(IReadOnlyList<StrategyPriceBar> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Count < 15)
            return new(null, 0, ["At least 15 completed Sensex candles are required."]);

        var bars = source.TakeLast(Math.Min(30, source.Count)).ToArray();
        var closes = bars.Select(x => x.Close).ToArray();
        var fast = TechnicalIndicators.ExponentialMovingAverage(closes, 9);
        var slow = TechnicalIndicators.ExponentialMovingAverage(closes, Math.Min(21, closes.Length));
        var atr = AverageTrueRange(bars.TakeLast(15).ToArray());
        if (atr <= 0) return new(null, 0, ["ATR is unavailable."]);

        var latest = bars[^1];
        var previous = bars[^2];
        var impulse = bars.SkipLast(2).TakeLast(7).ToArray();
        var separation = Math.Abs(fast - slow) / atr;
        var bullishTrend = fast > slow && impulse[^1].Close > impulse[0].Close + atr * .65m;
        var bearishTrend = fast < slow && impulse[^1].Close < impulse[0].Close - atr * .65m;
        var touchedFast = latest.Low <= fast + atr * .20m && previous.Low <= fast + atr * .35m;
        var touchedFastFromBelow = latest.High >= fast - atr * .20m && previous.High >= fast - atr * .35m;
        var bullishRejection = latest.Close > latest.Open && latest.Close > previous.Close &&
                               latest.Close >= fast && latest.Low > slow - atr * .15m;
        var bearishRejection = latest.Close < latest.Open && latest.Close < previous.Close &&
                               latest.Close <= fast && latest.High < slow + atr * .15m;
        var bullishExtension = (latest.Close - fast) / atr;
        var bearishExtension = (fast - latest.Close) / atr;

        if (separation >= .20m && bullishTrend && touchedFast && bullishRejection &&
            bullishExtension is >= 0m and <= .50m)
            return new(Direction.Buy, Math.Clamp(.55m + separation * .15m, .55m, .72m),
                ["EMA 9/21 establishes a bullish trend before the entry.",
                 "Price made a controlled pullback to EMA 9 without losing EMA 21.",
                 "The latest completed candle rejected the pullback before a new breakout became extended."]);

        if (separation >= .20m && bearishTrend && touchedFastFromBelow && bearishRejection &&
            bearishExtension is >= 0m and <= .80m)
            return new(Direction.Sell, Math.Clamp(.55m + separation * .15m, .55m, .72m),
                ["EMA 9/21 establishes a bearish trend before the entry.",
                 "Price made a controlled pullback to EMA 9 without reclaiming EMA 21.",
                 "The latest completed candle rejected the pullback before a new breakdown became extended."]);

        return new(null, .50m,
            ["No early trend-pullback rejection is present; the challenger will not manufacture an entry."]);
    }

    private static decimal AverageTrueRange(StrategyPriceBar[] bars)
    {
        var ranges = new List<decimal>();
        for (var i = 1; i < bars.Length; i++)
            ranges.Add(Math.Max(bars[i].High - bars[i].Low,
                Math.Max(Math.Abs(bars[i].High - bars[i - 1].Close),
                    Math.Abs(bars[i].Low - bars[i - 1].Close))));
        return ranges.Count == 0 ? 0 : ranges.Average();
    }
}
