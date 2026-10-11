using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Execution;

public sealed record ScalpPriceBar(DateTimeOffset TimeUtc, decimal Open, decimal High,
    decimal Low, decimal Close, long Volume);

public sealed record ScalpSignal(Direction? Direction, decimal Confidence,
    string Regime, decimal Atr, IReadOnlyList<string> Evidence);

public static class ScalpTradingPolicy
{
    public static ScalpSignal Evaluate(IReadOnlyList<ScalpPriceBar> bars)
    {
        if (bars.Count < 14) return new(null, 0m, "InsufficientData", 0m,
            ["At least fourteen completed one-minute candles are required."]);

        var closes = bars.Select(value => value.Close).ToArray();
        var fast = Ema(closes, 5);
        var slow = Ema(closes, 13);
        var atr = Atr(bars.TakeLast(14).ToArray());
        if (atr <= 0m) return new(null, 0m, "InvalidVolatility", 0m,
            ["A positive ATR is required."]);

        var latest = bars[^1];
        var previous = bars[^2];
        var priorRange = bars.TakeLast(7).SkipLast(1).ToArray();
        var priorHigh = priorRange.Max(value => value.High);
        var priorLow = priorRange.Min(value => value.Low);
        var threeBarMove = latest.Close - bars[^4].Close;
        var bullish = 0;
        var bearish = 0;
        var evidence = new List<string>();

        if (fast > slow) { bullish += 2; evidence.Add("EMA 5 is above EMA 13."); }
        if (fast < slow) { bearish += 2; evidence.Add("EMA 5 is below EMA 13."); }
        if (latest.Close > fast) { bullish++; evidence.Add("Price is above the fast EMA."); }
        if (latest.Close < fast) { bearish++; evidence.Add("Price is below the fast EMA."); }
        if (threeBarMove >= atr * .45m) { bullish++; evidence.Add("Three-candle momentum is bullish."); }
        if (threeBarMove <= -atr * .45m) { bearish++; evidence.Add("Three-candle momentum is bearish."); }
        if (latest.Close > priorHigh) { bullish += 2; evidence.Add("A six-candle high breakout closed."); }
        if (latest.Close < priorLow) { bearish += 2; evidence.Add("A six-candle low breakdown closed."); }
        if (latest.Close > latest.Open && previous.Close < previous.Open && latest.Close > previous.High)
        { bullish++; evidence.Add("A bullish reversal engulfed the previous candle."); }
        if (latest.Close < latest.Open && previous.Close > previous.Open && latest.Close < previous.Low)
        { bearish++; evidence.Add("A bearish reversal engulfed the previous candle."); }

        var extension = Math.Abs(latest.Close - slow) / atr;
        // ScalpLab is intentionally more permissive than the champion engine, but it still
        // refuses truly parabolic entries where the fast move is already likely exhausted.
        if (extension > 5m)
            return new(null, 0m, "Extended", atr,
                [.. evidence, $"Price is already extended {extension:F2} ATR from EMA 13."]);

        var score = Math.Max(bullish, bearish);
        var edge = Math.Abs(bullish - bearish);
        if (score < 3 || edge < 2)
            return new(null, Math.Min(.49m, score / 10m), "Mixed", atr,
                [.. evidence, $"Bullish score {bullish}; bearish score {bearish}."]);

        var direction = bullish > bearish ? Direction.Buy : Direction.Sell;
        var confidence = Math.Min(.90m, .50m + score * .05m + edge * .025m);
        return new(direction, confidence, fast > slow ? "FastBullish" : "FastBearish", atr,
            [.. evidence, $"Bullish score {bullish}; bearish score {bearish}."]);
    }

    public static decimal TrailingStop(decimal entry, decimal currentStop, decimal peak,
        decimal activationPoints, decimal trailingGapPoints, decimal netBreakEvenPrice)
    {
        if (peak - entry < activationPoints) return currentStop;
        return Math.Max(currentStop, Math.Max(netBreakEvenPrice, peak - trailingGapPoints));
    }

    private static decimal Ema(IReadOnlyList<decimal> values, int period)
    {
        var seed = values.Take(period).Average();
        var multiplier = 2m / (period + 1m);
        return values.Skip(period).Aggregate(seed, (value, close) =>
            (close - value) * multiplier + value);
    }

    private static decimal Atr(ScalpPriceBar[] bars)
    {
        if (bars.Length < 2) return 0m;
        var ranges = new List<decimal>();
        for (var index = 1; index < bars.Length; index++)
            ranges.Add(Math.Max(bars[index].High - bars[index].Low,
                Math.Max(Math.Abs(bars[index].High - bars[index - 1].Close),
                    Math.Abs(bars[index].Low - bars[index - 1].Close))));
        return ranges.Average();
    }
}
