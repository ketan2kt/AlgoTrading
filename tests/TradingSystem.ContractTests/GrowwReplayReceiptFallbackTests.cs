using TradingSystem.Application.Broker;
using TradingSystem.Infrastructure.MarketData;

namespace TradingSystem.ContractTests;

public sealed class GrowwReplayReceiptFallbackTests
{
    [Fact]
    public void TriggersOnlyTheMarketMissingFreshPersistedOptionDepth()
    {
        var now = new DateTimeOffset(2026, 11, 11, 10, 0, 0, TimeSpan.FromHours(5.5));
        var snapshot = new ReplayRecorderSnapshot(new(2026, 11, 11), now.AddSeconds(-5),
            new("Subscribed", now.AddHours(-1), 499, false, new Dictionary<string, DateTimeOffset>()),
            new HistoricalQuoteArchiveHealth(0, false, now, null)
            {
                LastOptionSourceAtByMarket = new Dictionary<string, DateTimeOffset>
                { ["NIFTY"] = now.AddSeconds(-6), ["BANKNIFTY"] = now.AddSeconds(-3), ["SENSEX"] = now.AddSeconds(-20) }
            }, now);
        Assert.False(GrowwReplayReceiptFallback.Stalled(snapshot, "NIFTY", now));
        Assert.False(GrowwReplayReceiptFallback.Stalled(snapshot, "BANKNIFTY", now));
        Assert.True(GrowwReplayReceiptFallback.Stalled(snapshot, "SENSEX", now));
        Assert.True(GrowwReplayReceiptFallback.Stalled(snapshot with { AtUtc = now.AddSeconds(-26) }, "NIFTY", now));
        Assert.False(GrowwReplayReceiptFallback.Stalled(snapshot with { AtUtc = now.AddSeconds(1) }, "SENSEX", now));
        Assert.True(GrowwReplayReceiptFallback.InWindow(now));
        Assert.False(GrowwReplayReceiptFallback.InWindow(now.AddHours(6)));
        Assert.False(GrowwReplayReceiptFallback.InWindow(now.AddDays(3)));
    }

    [Theory]
    [InlineData("NSE", "FNO", "NIFTYTESTCE", "NIFTY")]
    [InlineData("NSE", "FNO", "BANKNIFTYTESTPE", "BANKNIFTY")]
    [InlineData("BSE", "FNO", "SENSEXTESTCE", "SENSEX")]
    [InlineData("NSE", "FNO", "SENSEXTESTCE", null)]
    [InlineData("NSE", "CASH", "NIFTYTESTCE", null)]
    [InlineData("NSE", "FNO", "NIFTYTESTFUT", null)]
    public void MatchesOnlyIndexOptionContracts(string exchange, string segment, string symbol, string? expected) =>
        Assert.Equal(expected, GrowwReplayReceiptFallback.Market(new(exchange, segment, symbol)));
}
