namespace TradingSystem.Application.MarketData;

public sealed record FeedFreshnessResult(string Status, string? Message, bool IsFresh);

public static class FeedFreshnessPolicy
{
    public static FeedFreshnessResult Resolve(string status, string? message,
        DateTimeOffset? lastReceivedAtUtc, DateTimeOffset nowUtc, TimeSpan limit,
        DateTimeOffset? lastMarketTimestampUtc = null, string? pendingStage = null)
    {
        var receivedFresh = lastReceivedAtUtc.HasValue && lastReceivedAtUtc <= nowUtc.AddSeconds(1) &&
                            nowUtc - lastReceivedAtUtc.Value <= limit;
        var sourceFresh = !lastMarketTimestampUtc.HasValue ||
                          (lastMarketTimestampUtc <= nowUtc.AddSeconds(1) && nowUtc - lastMarketTimestampUtc.Value <= limit);
        var fresh = status == "Connected" && receivedFresh && sourceFresh;
        return !fresh && status == "Connected"
            ? new("DataStale",
                FormattableString.Invariant($"No validated quote within {limit.TotalSeconds:0} seconds. Last accepted source: {lastMarketTimestampUtc:O}; received: {lastReceivedAtUtc:O}. {pendingStage ?? "Waiting for the next valid quote."}"), false)
            : new(status, message, fresh);
    }
}
