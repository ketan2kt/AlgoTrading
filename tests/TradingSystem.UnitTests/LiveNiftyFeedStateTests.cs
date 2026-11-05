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

    [Theory]
    [InlineData(30, 29, true)]
    [InlineData(30, 30, true)]
    [InlineData(30, 31, false)]
    [InlineData(5, 4, true)]
    [InlineData(5, 5, true)]
    [InlineData(5, 6, false)]
    public void ConfiguredFreshnessBoundaryIsPreserved(int limit, int seconds, bool expected)
    {
        var at = new DateTimeOffset(2026, 11, 5, 4, 0, 0, TimeSpan.Zero);
        var state = FeedFreshnessPolicy.Resolve("Connected", null, at, at.AddSeconds(seconds),
            TimeSpan.FromSeconds(limit), at, "Persisting quote");
        Assert.Equal(expected, state.IsFresh);
        if (!expected) Assert.Contains("Persisting quote", state.Message);
    }

    [Fact]
    public void ReceivingAnOldQuoteNowDoesNotMakeItFresh()
    {
        var at = new DateTimeOffset(2026, 11, 5, 4, 0, 0, TimeSpan.Zero);
        Assert.False(FeedFreshnessPolicy.Resolve("Connected", null, at, at,
            TimeSpan.FromSeconds(30), at.AddMinutes(-1)).IsFresh);
        Assert.False(FeedFreshnessPolicy.Resolve("Disconnected", "Timeout", at, at,
            TimeSpan.FromSeconds(30), at).IsFresh);
        Assert.False(FeedFreshnessPolicy.Resolve("Connected", null, at.AddMinutes(1), at,
            TimeSpan.FromSeconds(30), at).IsFresh);
    }
}
