namespace TradingSystem.Infrastructure.MarketData;

using TradingSystem.Application.MarketData;

internal sealed class MultiMarketFeedState(TimeProvider timeProvider)
{
    private readonly object sync = new();
    private readonly Dictionary<string, LiveNiftyFeedSnapshot> snapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> received = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> stages = new(StringComparer.OrdinalIgnoreCase);

    public void RecordStage(string market, string value)
    {
        lock (sync) stages[market] = value;
    }

    public void RecordSuccess(string market, DateTimeOffset marketTimestampUtc, DateTimeOffset receivedAtUtc)
    {
        lock (sync)
        {
            snapshots[market] = new("Connected", null, marketTimestampUtc.ToUniversalTime(), true);
            received[market] = receivedAtUtc.ToUniversalTime();
            stages.Remove(market);
        }
    }

    public void RecordStatus(string market, string status, string? message)
    {
        lock (sync)
        {
            var prior = snapshots.GetValueOrDefault(market);
            var detail = status == "Disconnected" && stages.TryGetValue(market, out var stage)
                ? $"{message} Last stage: {stage}" : message;
            snapshots[market] = new(status, detail, prior?.LastMarketTimestampUtc, false);
        }
    }

    public LiveNiftyFeedSnapshot GetSnapshot(string market, TimeSpan freshnessLimit)
    {
        lock (sync)
        {
            var value = snapshots.GetValueOrDefault(market) ?? new("Starting", "Market feed is starting.", null, false);
            var freshness = FeedFreshnessPolicy.Resolve(value.Status, value.Message,
                received.TryGetValue(market, out var at) ? at : null,
                timeProvider.GetUtcNow(), freshnessLimit, value.LastMarketTimestampUtc, stages.GetValueOrDefault(market));
            return value with { Status = freshness.Status, Message = freshness.Message, IsFresh = freshness.IsFresh };
        }
    }
}
