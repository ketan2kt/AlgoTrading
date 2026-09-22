namespace TradingSystem.Application.MarketData;

public sealed record WorkspaceTelemetry(decimal CurrentPrice, decimal OpeningRangeHigh,
    decimal OpeningRangeLow, decimal Vwap, decimal FastEma, decimal SlowEma,
    decimal AtrPercent, decimal RelativeFuturesVolume);

public static class WorkspaceTelemetryCalculator
{
    public static WorkspaceTelemetry At(IReadOnlyList<WorkspaceCandle> underlying,
        IReadOnlyList<WorkspaceCandle> futures, DateTimeOffset timestamp,
        DateTimeOffset sessionStartUtc)
    {
        var bars = underlying.Where(x => x.OpenTimeUtc >= sessionStartUtc &&
                                         x.OpenTimeUtc <= timestamp).OrderBy(x => x.OpenTimeUtc).ToArray();
        var futureBars = futures.Where(x => x.OpenTimeUtc >= sessionStartUtc &&
                                            x.OpenTimeUtc <= timestamp).OrderBy(x => x.OpenTimeUtc).ToArray();
        if (bars.Length == 0) return new(0, 0, 0, 0, 0, 0, 0, 0);
        var opening = bars.Where(x => x.OpenTimeUtc >= sessionStartUtc &&
                                      x.OpenTimeUtc < sessionStartUtc.AddMinutes(15)).ToArray();
        var closes = bars.Select(x => x.Close).ToArray();
        var fast = TechnicalIndicators.ExponentialMovingAverage(closes, Math.Min(9, closes.Length));
        var slow = TechnicalIndicators.ExponentialMovingAverage(closes, Math.Min(21, closes.Length));
        var atr = AverageTrueRange(bars.TakeLast(Math.Min(15, bars.Length)).ToArray());
        var vwapSource = futureBars.Any(x => x.Volume > 0) ? futureBars : bars;
        var totalVolume = vwapSource.Sum(x => (decimal)Math.Max(0, x.Volume));
        var vwap = totalVolume <= 0 ? 0 : vwapSource.Sum(x =>
            ((x.High + x.Low + x.Close) / 3m) * Math.Max(0, x.Volume)) / totalVolume;
        var positiveVolumes = futureBars.Where(x => x.Volume > 0).TakeLast(21).ToArray();
        var relative = positiveVolumes.Length < 2 ? 0 :
            (decimal)positiveVolumes[^1].Volume / positiveVolumes.SkipLast(1).Average(x => (decimal)x.Volume);
        return new(bars[^1].Close, opening.Length == 0 ? 0 : opening.Max(x => x.High),
            opening.Length == 0 ? 0 : opening.Min(x => x.Low), vwap, fast, slow,
            bars[^1].Close <= 0 ? 0 : atr / bars[^1].Close * 100m, relative);
    }

    private static decimal AverageTrueRange(WorkspaceCandle[] bars)
    {
        if (bars.Length < 2) return 0;
        var values = new List<decimal>();
        for (var i = 1; i < bars.Length; i++)
            values.Add(Math.Max(bars[i].High - bars[i].Low,
                Math.Max(Math.Abs(bars[i].High - bars[i - 1].Close),
                    Math.Abs(bars[i].Low - bars[i - 1].Close))));
        return values.Average();
    }
}
