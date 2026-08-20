namespace TradingSystem.Infrastructure.MarketData;

internal sealed class MultiMarketFeedState(TimeProvider timeProvider)
{
    private readonly object sync = new();
    private readonly Dictionary<string, LiveNiftyFeedSnapshot> snapshots = new(StringComparer.OrdinalIgnoreCase);

    public void RecordSuccess(string market, DateTimeOffset marketTimestampUtc, DateTimeOffset receivedAtUtc)
    {
        lock (sync)
            snapshots[market] = new("Connected", null, marketTimestampUtc.ToUniversalTime(), true);
    }

    public void RecordStatus(string market, string status, string? message)
    {
        lock (sync)
        {
            var prior = snapshots.GetValueOrDefault(market);
            snapshots[market] = new(status, message, prior?.LastMarketTimestampUtc, false);
        }
    }

    public LiveNiftyFeedSnapshot GetSnapshot(string market, TimeSpan freshnessLimit)
    {
        lock (sync)
        {
            var value = snapshots.GetValueOrDefault(market) ?? new("Starting", "Market feed is starting.", null, false);
            var fresh = value.LastMarketTimestampUtc.HasValue &&
                        timeProvider.GetUtcNow() - value.LastMarketTimestampUtc.Value <= freshnessLimit;
            return value with { IsFresh = fresh };
        }
    }
}
