using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Execution;

public sealed record ScalpPriceBar(DateTimeOffset TimeUtc, decimal Open, decimal High,
    decimal Low, decimal Close, long Volume);

public sealed record ScalpSignal(Direction? Direction, decimal Confidence,
    string Regime, decimal Atr, IReadOnlyList<string> Evidence);

public static class ScalpTradingPolicy
{
    public static IReadOnlyList<ScalpPriceBar> AggregateCompletedBars(
        IReadOnlyList<ScalpPriceBar> oneMinuteBars, int timeframeMinutes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(timeframeMinutes, 2);
        var seconds = timeframeMinutes * 60L;
        return oneMinuteBars.OrderBy(value => value.TimeUtc)
            .GroupBy(value => value.TimeUtc.ToUnixTimeSeconds() / seconds)
            .Where(group =>
            {
                var ordered = group.OrderBy(value => value.TimeUtc).ToArray();
                return ordered.Length == timeframeMinutes &&
                       ordered.Select(value => value.TimeUtc).Distinct().Count() == timeframeMinutes &&
                       ordered.Zip(ordered.Skip(1), (left, right) => right.TimeUtc - left.TimeUtc)
                           .All(gap => gap == TimeSpan.FromMinutes(1));
            })
            .Select(group =>
            {
                var ordered = group.OrderBy(value => value.TimeUtc).ToArray();
                return new ScalpPriceBar(DateTimeOffset.FromUnixTimeSeconds(group.Key * seconds),
                    ordered[0].Open, ordered.Max(value => value.High), ordered.Min(value => value.Low),
                    ordered[^1].Close, ordered.Sum(value => value.Volume));
            })
            .ToArray();
    }

    public static ScalpSignal Evaluate(IReadOnlyList<ScalpPriceBar> bars)
    {
        if (bars.Count < 14) return new(null, 0m, "InsufficientData", 0m,
            ["At least fourteen completed signal-timeframe candles are required."]);

        var closes = bars.Select(value => value.Close).ToArray();
        var fast = Ema(closes, 5);
        var slow = Ema(closes, 13);
        var previousFast = Ema(closes[..^1], 5);
        var previousSlow = Ema(closes[..^1], 13);
        var atr = Atr(bars.TakeLast(14).ToArray());
        if (atr <= 0m) return new(null, 0m, "InvalidVolatility", 0m,
            ["A positive ATR is required."]);

        var latest = bars[^1];
        var previous = bars[^2];
        var priorRange = bars.TakeLast(9).SkipLast(1).ToArray();
        var priorHigh = priorRange.Max(value => value.High);
        var priorLow = priorRange.Min(value => value.Low);
        var averageVolume = bars.TakeLast(11).SkipLast(1).Where(value => value.Volume > 0)
            .Select(value => (decimal)value.Volume).DefaultIfEmpty(0m).Average();
        var volumeConfirmed = averageVolume == 0m || latest.Volume >= averageVolume * 1.10m;
        var body = Math.Abs(latest.Close - latest.Open);
        var range = latest.High - latest.Low;
        var bullishClose = latest.Close > latest.Open && latest.High - latest.Close <= range * .35m;
        var bearishClose = latest.Close < latest.Open && latest.Close - latest.Low <= range * .35m;
        var bullishTrend = fast > slow && fast > previousFast && slow >= previousSlow;
        var bearishTrend = fast < slow && fast < previousFast && slow <= previousSlow;
        var bullishBreakout = bullishTrend && latest.Close > priorHigh && bullishClose &&
                              body >= atr * .25m && volumeConfirmed;
        var bearishBreakdown = bearishTrend && latest.Close < priorLow && bearishClose &&
                               body >= atr * .25m && volumeConfirmed;
        var bullishPullback = bullishTrend && previous.Low <= previousFast + atr * .20m &&
                              latest.Low <= fast + atr * .25m && bullishClose && latest.Close > previous.High;
        var bearishPullback = bearishTrend && previous.High >= previousFast - atr * .20m &&
                              latest.High >= fast - atr * .25m && bearishClose && latest.Close < previous.Low;
        var evidence = new List<string>();

        if (bullishTrend) evidence.Add("EMA 5 and EMA 13 are rising in bullish alignment.");
        if (bearishTrend) evidence.Add("EMA 5 and EMA 13 are falling in bearish alignment.");
        if (bullishBreakout) evidence.Add("An eight-bar high breakout closed strongly with volume confirmation.");
        if (bearishBreakdown) evidence.Add("An eight-bar low breakdown closed strongly with volume confirmation.");
        if (bullishPullback) evidence.Add("A bullish fast-EMA pullback reclaimed the previous candle high.");
        if (bearishPullback) evidence.Add("A bearish fast-EMA pullback broke the previous candle low.");

        var extension = Math.Abs(latest.Close - slow) / atr;
        if (extension > 3m)
            return new(null, 0m, "Extended", atr,
                [.. evidence, $"Price is already extended {extension:F2} ATR from EMA 13."]);

        var bullishSetup = bullishBreakout || bullishPullback;
        var bearishSetup = bearishBreakdown || bearishPullback;
        if (bullishSetup == bearishSetup)
            return new(null, 0m, "NoStructuredSetup", atr,
                [.. evidence, "A confirmed breakout or EMA pullback-reclaim is required."]);

        var direction = bullishSetup ? Direction.Buy : Direction.Sell;
        var breakout = bullishBreakout || bearishBreakdown;
        var confidence = breakout ? .76m : .71m;
        if (volumeConfirmed && averageVolume > 0m) confidence += .03m;
        return new(direction, Math.Min(.85m, confidence), breakout ? "StructuredBreakout" : "StructuredPullback", atr,
            [.. evidence, $"Extension is {extension:F2} ATR from EMA 13."]);
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
