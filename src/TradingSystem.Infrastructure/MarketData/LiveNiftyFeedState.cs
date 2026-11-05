namespace TradingSystem.Infrastructure.MarketData;

using TradingSystem.Application.MarketData;

internal sealed class LiveNiftyFeedState(TimeProvider timeProvider)
{
    private readonly object sync = new();
    private string status = "Disabled";
    private string? message = "Live Nifty ingestion is disabled.";
    private DateTimeOffset? lastMarketTimestampUtc;
    private DateTimeOffset? lastReceivedAtUtc;
    private string? pendingStage;

    public void RecordStage(string value)
    {
        lock (sync) pendingStage = value;
    }

    public void RecordSuccess(DateTimeOffset marketTimestampUtc, DateTimeOffset receivedAtUtc)
    {
        lock (sync)
        {
            status = "Connected";
            message = null;
            pendingStage = null;
            lastMarketTimestampUtc = marketTimestampUtc.ToUniversalTime();
            lastReceivedAtUtc = receivedAtUtc.ToUniversalTime();
        }
    }

    public void RecordStatus(string value, string? detail)
    {
        lock (sync)
        {
            status = value;
            message = value == "Disconnected" && pendingStage is not null ? $"{detail} Last stage: {pendingStage}" : detail;
        }
    }

    public LiveNiftyFeedSnapshot GetSnapshot(TimeSpan freshnessLimit)
    {
        lock (sync)
        {
            var result = FeedFreshnessPolicy.Resolve(status, message, lastReceivedAtUtc,
                timeProvider.GetUtcNow(), freshnessLimit, lastMarketTimestampUtc, pendingStage);
            return new(result.Status, result.Message, lastMarketTimestampUtc, result.IsFresh);
        }
    }
}

internal sealed record LiveNiftyFeedSnapshot(
    string Status,
    string? Message,
    DateTimeOffset? LastMarketTimestampUtc,
    bool IsFresh);
