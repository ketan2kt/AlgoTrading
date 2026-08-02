namespace TradingSystem.Application.MarketData;

public static class TechnicalIndicators
{
    public static decimal SimpleMovingAverage(IReadOnlyList<decimal> values, int period)
    {
        ValidatePeriod(values, period);
        return values.TakeLast(period).Average();
    }

    public static decimal ExponentialMovingAverage(IReadOnlyList<decimal> values, int period)
    {
        ValidatePeriod(values, period);
        var multiplier = 2m / (period + 1m);
        var ema = values.Take(period).Average();
        for (var index = period; index < values.Count; index++)
            ema = ((values[index] - ema) * multiplier) + ema;
        return ema;
    }

    public static decimal VolumeWeightedAveragePrice(IReadOnlyList<CompletedCandle> candles)
    {
        ArgumentNullException.ThrowIfNull(candles);
        var volume = candles.Sum(candle => candle.Volume);
        if (volume <= 0) throw new ArgumentException("Positive volume is required.", nameof(candles));
        return candles.Sum(candle => ((candle.High + candle.Low + candle.Close) / 3m) * candle.Volume) / volume;
    }

    public static decimal AverageTrueRange(IReadOnlyList<CompletedCandle> candles, int period)
    {
        ArgumentNullException.ThrowIfNull(candles);
        if (period <= 0 || candles.Count < period + 1) throw new ArgumentOutOfRangeException(nameof(period));
        var ranges = new List<decimal>(period);
        for (var index = candles.Count - period; index < candles.Count; index++)
        {
            var current = candles[index];
            var previousClose = candles[index - 1].Close;
            ranges.Add(Math.Max(current.High - current.Low,
                Math.Max(Math.Abs(current.High - previousClose), Math.Abs(current.Low - previousClose))));
        }
        return ranges.Average();
    }

    private static void ValidatePeriod(IReadOnlyList<decimal> values, int period)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (period <= 0 || values.Count < period) throw new ArgumentOutOfRangeException(nameof(period));
    }
}
