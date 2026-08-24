using TradingSystem.Application.MarketData;
using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Strategies;

public sealed record IndexMomentumDecision(
    Direction? Direction,
    decimal Confidence,
    IReadOnlyList<string> Reasons);

/// <summary>
/// Applies the same completed-candle momentum quality rules to cash indices.
/// It rejects weak/noisy breaks and exhausted single-candle spikes.
/// </summary>
public static class IndexMomentumBreakoutPolicy
{
    public static IndexMomentumDecision Evaluate(IReadOnlyList<Candle> candles)
    {
        ArgumentNullException.ThrowIfNull(candles);
        if (candles.Count < 21)
            return new(null, 0m, ["At least 21 completed candles are required."]);

        var recent = candles.TakeLast(21).ToArray();
        var latest = recent[^1];
        var prior = recent.TakeLast(6).SkipLast(1).ToArray();
        var atr = AverageTrueRange(recent.TakeLast(15).ToArray());
        if (atr <= 0 || latest.Close <= 0)
            return new(null, 0m, ["ATR or closing price is unavailable."]);

        var closes = recent.Select(value => value.Close).ToArray();
        var fast = TechnicalIndicators.ExponentialMovingAverage(closes, 9);
        var slow = TechnicalIndicators.ExponentialMovingAverage(closes, 21);
        var body = Math.Abs(latest.Close - latest.Open);
        var range = latest.High - latest.Low;
        var impulseRatio = body / atr;
        var closesNearHigh = range > 0 && (latest.High - latest.Close) / range <= 0.30m;
        var closesNearLow = range > 0 && (latest.Close - latest.Low) / range <= 0.30m;
        var recentBullishCloses = recent.TakeLast(3).Count(value => value.Close > value.Open);
        var recentBearishCloses = recent.TakeLast(3).Count(value => value.Close < value.Open);

        var impulseIsQualified = impulseRatio is >= 0.45m and <= 1.80m;
        var bullish = fast > slow && latest.Close > prior.Max(value => value.High) &&
                      latest.Close > latest.Open && closesNearHigh && recentBullishCloses >= 2 &&
                      impulseIsQualified;
        var bearish = fast < slow && latest.Close < prior.Min(value => value.Low) &&
                      latest.Close < latest.Open && closesNearLow && recentBearishCloses >= 2 &&
                      impulseIsQualified;
        var separation = Math.Abs(fast - slow) / latest.Close;
        var confidence = Math.Clamp(0.50m + separation * 100m +
                                    Math.Min(impulseRatio, 1m) * 0.15m, 0m, 0.90m);

        if (bullish)
            return new(Direction.Buy, confidence,
                ["EMA 9 is above EMA 21.", "A completed candle broke five-candle structure with confirmed bullish impulse."]);
        if (bearish)
            return new(Direction.Sell, confidence,
                ["EMA 9 is below EMA 21.", "A completed candle broke five-candle structure with confirmed bearish impulse."]);

        return new(null, confidence,
            ["No qualified EMA-aligned breakout: structure, candle close quality, persistence, or impulse filter failed."]);
    }

    private static decimal AverageTrueRange(Candle[] candles)
    {
        var ranges = new List<decimal>(candles.Length - 1);
        for (var index = 1; index < candles.Length; index++)
            ranges.Add(Math.Max(candles[index].High - candles[index].Low,
                Math.Max(Math.Abs(candles[index].High - candles[index - 1].Close),
                    Math.Abs(candles[index].Low - candles[index - 1].Close))));
        return ranges.Count == 0 ? 0m : ranges.Average();
    }
}
