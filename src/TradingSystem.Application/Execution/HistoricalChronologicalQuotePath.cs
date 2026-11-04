using System.Collections;

namespace TradingSystem.Application.Execution;

// Re-readable, immutable, time/symbol ordered input. Infrastructure may keep
// its pages on disk; policies still see only the current as-of frame.
public interface IHistoricalChronologicalQuotePath : IReadOnlyList<HistoricalReplayQuote>;

public sealed record HistoricalQuotePathReference(string Id, int Count);

public sealed class HistoricalQuoteOrderComparer : IComparer<HistoricalReplayQuote>
{
    public static HistoricalQuoteOrderComparer Instance { get; } = new();
    public int Compare(HistoricalReplayQuote? x, HistoricalReplayQuote? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;
        var time = x.AvailableAtUtc.CompareTo(y.AvailableAtUtc);
        return time != 0 ? time : StringComparer.Ordinal.Compare(x.Symbol, y.Symbol);
    }
}

public static class HistoricalQuotePaths
{
    public static IEnumerable<HistoricalReplayQuote> Ordered(IReadOnlyList<HistoricalReplayQuote> path) =>
        path is IHistoricalChronologicalQuotePath ? path : path.OrderBy(value => value, HistoricalQuoteOrderComparer.Instance);

    public static IHistoricalChronologicalQuotePath Merge(IEnumerable<IReadOnlyList<HistoricalReplayQuote>> paths) =>
        new MergedPath(paths.ToArray());

    private sealed class MergedPath(IReadOnlyList<HistoricalReplayQuote>[] paths) : IHistoricalChronologicalQuotePath
    {
        public int Count { get; } = checked(paths.Sum(value => value.Count));
        public HistoricalReplayQuote this[int index] => index >= 0 && index < Count ? this.Skip(index).First() : throw new ArgumentOutOfRangeException(nameof(index));
        public IEnumerator<HistoricalReplayQuote> GetEnumerator()
        {
            var cursors = paths.Select(value => Ordered(value).GetEnumerator()).ToArray();
            var queue = new PriorityQueue<IEnumerator<HistoricalReplayQuote>, HistoricalReplayQuote>(HistoricalQuoteOrderComparer.Instance);
            try
            {
                foreach (var cursor in cursors) if (cursor.MoveNext()) queue.Enqueue(cursor, cursor.Current);
                while (queue.TryDequeue(out var cursor, out _))
                {
                    yield return cursor.Current;
                    if (cursor.MoveNext()) queue.Enqueue(cursor, cursor.Current);
                }
            }
            finally { foreach (var cursor in cursors) cursor.Dispose(); }
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
