using TradingSystem.Application.Strategies;

namespace TradingSystem.Application.Execution;

public sealed record NiftySignalFeatures(decimal RelativeFuturesVolume,
    decimal Vwap, decimal FastEma, decimal SlowEma, decimal AtrPercent,
    decimal OpeningRangeHigh, decimal OpeningRangeLow);

// One market-feature calculation for the paper decision and the historical
// adapter. Inputs are the same session's as-of spot candles and futures
// candles aligned through the most recent spot candle.
public static class NiftySignalFeaturePolicy
{
    private static readonly TimeSpan IndiaOffset = TimeSpan.FromHours(5.5);

    public static MarketStructureQualitySnapshot EntryStructure(StrategyEvaluationContext context) =>
        MarketStructureQualityAnalyzer.Analyze(context.RecentCandles, null,
            context.CurrentPrice, context.Vwap, context.AtrPercent,
            context.OpeningRangeHigh, context.OpeningRangeLow);

    public static NiftySignalFeatures? Calculate(
        IReadOnlyList<StrategyPriceBar> spot,
        IReadOnlyList<StrategyPriceBar> alignedFutures,
        TimeOnly openingRangeEnd)
    {
        ArgumentNullException.ThrowIfNull(spot);
        ArgumentNullException.ThrowIfNull(alignedFutures);
        if (spot.Count < 2 || alignedFutures.Count < 2) return null;
        var opening = spot.Where(value => TimeOnly.FromDateTime(
            value.OpenTimeUtc.ToOffset(IndiaOffset).DateTime) < openingRangeEnd)
            .ToArray();
        var totalVolume = alignedFutures.Sum(value => value.Volume);
        // Compare participation with recent completed minutes, not the opening
        // auction/first-minute spike for the entire remaining session. Keep the
        // current candle out of the reference window and retain zero-volume bars.
        var averageVolume = alignedFutures.SkipLast(1).TakeLast(20).Average(value =>
            value.Volume);
        if (opening.Length == 0 || totalVolume <= 0m ||
            averageVolume <= 0m) return null;
        var confirmationVolume = alignedFutures.ToDictionary(value =>
            value.OpenTimeUtc, value => value.Volume);
        var weightedSpot = spot.Where(value => confirmationVolume.ContainsKey(
            value.OpenTimeUtc)).ToArray();
        var alignedVolume = weightedSpot.Sum(value =>
            confirmationVolume[value.OpenTimeUtc]);
        if (alignedVolume <= 0m) return null;
        var vwap = weightedSpot.Sum(value =>
            ((value.High + value.Low + value.Close) / 3m) *
            confirmationVolume[value.OpenTimeUtc]) / alignedVolume;
        var closes = spot.Select(value => value.Close).ToArray();
        var last = spot.TakeLast(15).ToArray();
        var ranges = new List<decimal>();
        for (var index = 1; index < last.Length; index++)
            ranges.Add(Math.Max(last[index].High - last[index].Low,
                Math.Max(Math.Abs(last[index].High - last[index - 1].Close),
                    Math.Abs(last[index].Low - last[index - 1].Close))));
        var atrPercent = ranges.Average() / last[^1].Close * 100m;
        return new(alignedFutures[^1].Volume / averageVolume, vwap,
            Ema(closes, 9), Ema(closes, 21), atrPercent,
            opening.Max(value => value.High),
            opening.Min(value => value.Low));
    }

    private static decimal Ema(decimal[] values, int period)
    {
        var multiplier = 2m / (period + 1m);
        var result = values[0];
        for (var index = 1; index < values.Length; index++)
            result = (values[index] - result) * multiplier + result;
        return result;
    }
}
