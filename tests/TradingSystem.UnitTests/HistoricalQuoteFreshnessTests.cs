using TradingSystem.Application.Execution;

namespace TradingSystem.UnitTests;

public sealed class HistoricalQuoteFreshnessTests
{
    [Fact]
    public void FreshnessUsesObservedReceiptLikeProductionButRejectsFutureSources()
    {
        var now = new DateTimeOffset(2026, 11, 4, 4, 0, 0, TimeSpan.Zero);
        var quote = new HistoricalReplayQuote("NIFTYCE", now, 100) { SourceAtUtc = now, DepthSourceAtUtc = now };
        var age = TimeSpan.FromSeconds(5);
        Assert.True(quote.IsFreshAsOf(now.AddSeconds(5), age));
        Assert.True((quote with { SourceAtUtc = now.AddSeconds(-6) }).IsFreshAsOf(now, age));
        Assert.True((quote with { DepthSourceAtUtc = now.AddSeconds(-6) }).IsFreshAsOf(now, age));
        Assert.False(quote.IsFreshAsOf(now.AddSeconds(6), age));
        Assert.False((quote with { SourceAtUtc = now.AddTicks(1) }).IsFreshAsOf(now, age));
        Assert.False(quote.IsFreshAsOf(now.AddTicks(-1), age));
        Assert.True((quote with { SourceAtUtc = null, DepthSourceAtUtc = null }).IsFreshAsOf(now, age));
    }
}
