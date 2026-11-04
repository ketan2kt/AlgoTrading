namespace TradingSystem.Application.Execution;

public static class HistoricalArchivePathCanonicalizer
{
    public static HistoricalReplayBar[] PreferRecordedBars(IEnumerable<HistoricalReplayBar> recorded,
        IEnumerable<HistoricalReplayBar> repairs)
    {
        var originals = Bars(recorded);
        var keys = originals.Select(value => (value.Symbol, value.OpenTimeUtc, value.IntervalSeconds)).ToHashSet();
        return originals.Concat(Bars(repairs.Where(value => !keys.Contains((value.Symbol, value.OpenTimeUtc, value.IntervalSeconds)))))
            .OrderBy(value => value.AvailableAtUtc).ThenBy(value => value.Symbol, StringComparer.Ordinal).ToArray();
    }
    // The same official minute can be imported through mapped and research
    // paths. Identical observations collapse; conflicting revisions fail
    // closed instead of letting input order change replay results.
    public static HistoricalReplayBar[] Bars(
        IEnumerable<HistoricalReplayBar> source) => source
        .GroupBy(value => (value.Symbol, value.OpenTimeUtc,
            value.IntervalSeconds))
        .Select(group =>
        {
            var first = group.First();
            if (group.Any(value => value.Open != first.Open ||
                value.High != first.High || value.Low != first.Low ||
                value.Close != first.Close || value.Volume != first.Volume ||
                value.OpenInterest != first.OpenInterest))
                throw new InvalidOperationException(
                    $"Conflicting historical bar revisions for {first.Symbol} at {first.OpenTimeUtc:O}.");
            return first;
        }).ToArray();

    public static HistoricalReplayQuote[] Quotes(
        IEnumerable<HistoricalReplayQuote> source) => source
        .GroupBy(value => (value.Symbol, value.AvailableAtUtc))
        .Select(group =>
        {
            var first = group.First();
            if (group.Any(value => value.LastPrice != first.LastPrice ||
                value.Source != first.Source ||
                value.BidPrice != first.BidPrice ||
                value.OfferPrice != first.OfferPrice ||
                value.Volume != first.Volume ||
                value.OpenInterest != first.OpenInterest || value.SourceAtUtc != first.SourceAtUtc ||
                value.DepthSourceAtUtc != first.DepthSourceAtUtc))
                throw new InvalidOperationException(
                    $"Conflicting historical quote revisions for {first.Symbol} at {first.AvailableAtUtc:O}.");
            return first;
        }).ToArray();
}
