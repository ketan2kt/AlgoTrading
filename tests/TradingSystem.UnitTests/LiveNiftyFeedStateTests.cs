using TradingSystem.Application.MarketData;

namespace TradingSystem.UnitTests;

public sealed class LiveNiftyFeedStateTests
{
    [Fact]
    public void ConnectedSnapshotBecomesDataStaleAfterFreshnessWindow()
    {
        var received = new DateTimeOffset(2026, 9, 9, 4, 0, 0, TimeSpan.Zero);
        var stale = FeedFreshnessPolicy.Resolve("Connected", null, received,
            received.AddSeconds(6), TimeSpan.FromSeconds(5));
        Assert.Equal("DataStale", stale.Status);
        Assert.False(stale.IsFresh);
    }

    [Fact]
    public void ExplicitFailureStatusIsPreserved()
    {
        var state = FeedFreshnessPolicy.Resolve("Disconnected", "failed", null,
            DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
        Assert.Equal("Disconnected", state.Status);
    }
}
