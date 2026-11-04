using TradingSystem.Application.Strategies;

namespace TradingSystem.Application.Execution;

public sealed record SensexFutureVolumeBar(DateTimeOffset OpenTimeUtc, long Volume);

public sealed record SensexFutureConfirmation(decimal Vwap,
    decimal RelativeVolume)
{
    public bool Ready => Vwap > 0m && RelativeVolume > 0m;
}

// Shared pre-entry calculation. Only data available at the completed index
// candle may be passed; the caller remains responsible for that as-of gate.
public static class SensexFutureConfirmationPolicy
{
    public static SensexFutureConfirmation Evaluate(
        IReadOnlyList<StrategyPriceBar> indexBars,
        IReadOnlyList<SensexFutureVolumeBar> futureBars)
    {
        ArgumentNullException.ThrowIfNull(indexBars);
        ArgumentNullException.ThrowIfNull(futureBars);
        if (futureBars.Count == 0 || indexBars.Count == 0)
            return new(0m, 0m);
        if (indexBars.Zip(indexBars.Skip(1)).Any(pair =>
                pair.First.OpenTimeUtc >= pair.Second.OpenTimeUtc) ||
            futureBars.Zip(futureBars.Skip(1)).Any(pair =>
                pair.First.OpenTimeUtc >= pair.Second.OpenTimeUtc))
            throw new ArgumentException("Confirmation bars must be time-ordered and unique.");
        if (futureBars.Any(value => value.OpenTimeUtc > indexBars[^1].OpenTimeUtc))
            throw new ArgumentException(
                "Future bars cannot be newer than the evaluated index candle.",
                nameof(futureBars));
        var volumeByTime = futureBars.Where(value => value.Volume > 0)
            .ToDictionary(value => value.OpenTimeUtc, value => value.Volume);
        var aligned = indexBars.Where(value =>
            volumeByTime.ContainsKey(value.OpenTimeUtc)).ToArray();
        var totalVolume = aligned.Sum(value => volumeByTime[value.OpenTimeUtc]);
        var latestFuture = futureBars.LastOrDefault(value => value.Volume > 0);
        var baseline = futureBars.Where(value => value.Volume > 0)
            .TakeLast(Math.Min(20, futureBars.Count)).SkipLast(1).ToArray();
        var averageVolume = baseline.Length == 0 ? 0m :
            baseline.Average(value => (decimal)value.Volume);
        var relativeVolume = latestFuture is null || averageVolume <= 0m
            ? 0m : latestFuture.Volume / averageVolume;
        var vwap = totalVolume <= 0m ? 0m : aligned.Sum(value =>
            ((value.High + value.Low + value.Close) / 3m) *
            volumeByTime[value.OpenTimeUtc]) / totalVolume;
        return new(vwap, relativeVolume);
    }
}
