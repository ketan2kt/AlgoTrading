namespace TradingSystem.Infrastructure.MarketData;

internal sealed class LiveNiftyFeedState(TimeProvider timeProvider)
{
    private readonly object sync = new();
    private string status = "Disabled";
    private string? message = "Live Nifty ingestion is disabled.";
    private DateTimeOffset? lastMarketTimestampUtc;
    private DateTimeOffset? lastReceivedAtUtc;

    public void RecordSuccess(DateTimeOffset marketTimestampUtc, DateTimeOffset receivedAtUtc)
    {
        lock (sync)
        {
            status = "Connected";
            message = null;
            lastMarketTimestampUtc = marketTimestampUtc.ToUniversalTime();
            lastReceivedAtUtc = receivedAtUtc.ToUniversalTime();
        }
    }

    public void RecordStatus(string value, string? detail)
    {
        lock (sync)
        {
            status = value;
            message = detail;
        }
    }

    public LiveNiftyFeedSnapshot GetSnapshot(TimeSpan freshnessLimit)
    {
        lock (sync)
        {
            var fresh = lastReceivedAtUtc.HasValue &&
                        timeProvider.GetUtcNow() - lastReceivedAtUtc.Value <= freshnessLimit;
            return new(status, message, lastMarketTimestampUtc, fresh);
        }
    }
}

internal sealed record LiveNiftyFeedSnapshot(
    string Status,
    string? Message,
    DateTimeOffset? LastMarketTimestampUtc,
    bool IsFresh);
