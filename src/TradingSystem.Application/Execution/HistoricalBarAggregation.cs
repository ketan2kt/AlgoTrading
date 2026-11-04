namespace TradingSystem.Application.Execution;

public static class HistoricalBarAggregation
{
    // A five-minute bar exists only when all five source minutes exist. Its
    // availability is the fifth minute's close; incomplete buckets are omitted.
    public static IReadOnlyList<HistoricalReplayBar> ToFiveMinutes(
        IEnumerable<HistoricalReplayBar> minuteBars)
    {
        ArgumentNullException.ThrowIfNull(minuteBars);
        var result = new List<HistoricalReplayBar>();
        foreach (var group in minuteBars.Where(value => value.IntervalSeconds == 60 &&
                     !string.IsNullOrWhiteSpace(value.Symbol))
                 .GroupBy(value => (value.Symbol,
                     Bucket: new DateTimeOffset(value.OpenTimeUtc.Ticks -
                         value.OpenTimeUtc.Ticks % TimeSpan.FromMinutes(5).Ticks,
                         value.OpenTimeUtc.Offset))))
        {
            var source = group.OrderBy(value => value.OpenTimeUtc).ToArray();
            if (source.Length != 5 || source.Select(value => value.OpenTimeUtc)
                    .Distinct().Count() != 5 ||
                source.Where((value, index) =>
                    value.OpenTimeUtc != group.Key.Bucket.AddMinutes(index)).Any())
                continue;
            result.Add(new(group.Key.Bucket, 300, source[0].Open,
                source.Max(value => value.High), source.Min(value => value.Low),
                source[^1].Close)
            {
                Symbol = group.Key.Symbol,
                Volume = source.Sum(value => value.Volume),
                OpenInterest = source[^1].OpenInterest
            });
        }
        return result.OrderBy(value => value.OpenTimeUtc)
            .ThenBy(value => value.Symbol, StringComparer.Ordinal).ToArray();
    }
}
