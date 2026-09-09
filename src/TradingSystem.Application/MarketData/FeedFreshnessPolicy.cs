namespace TradingSystem.Application.MarketData;

public sealed record FeedFreshnessResult(string Status, string? Message, bool IsFresh);

public static class FeedFreshnessPolicy
{
    public static FeedFreshnessResult Resolve(string status, string? message,
        DateTimeOffset? lastReceivedAtUtc, DateTimeOffset nowUtc, TimeSpan limit)
    {
        var fresh = lastReceivedAtUtc.HasValue && nowUtc - lastReceivedAtUtc.Value <= limit;
        return !fresh && status == "Connected"
            ? new("DataStale",
                "Nifty has not received a fresh quote; automation is blocked until polling recovers.", false)
            : new(status, message, fresh);
    }
}
