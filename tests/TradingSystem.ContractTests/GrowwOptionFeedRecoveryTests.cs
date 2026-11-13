using TradingSystem.Infrastructure.MarketData;

namespace TradingSystem.ContractTests;

public sealed class GrowwOptionFeedRecoveryTests
{
    private static readonly DateTimeOffset Open = new(2026, 11, 11, 3, 45, 0, TimeSpan.Zero);
    private static readonly string[] Markets = ["NIFTY", "SENSEX", "BANKNIFTY"];
    private static readonly string[] Channels = ["price", "depth"];

    [Theory]
    [InlineData("NIFTY", "price")]
    [InlineData("SENSEX", "depth")]
    [InlineData("BANKNIFTY", "price")]
    public void RenewsOnlyTheStalledOptionChannelBeforeTheGlobalWatchdog(string market, string kind)
    {
        var groups = (from index in Markets
                      from channel in Channels
                      select (index, channel)).ToDictionary(key => key, _ => new GrowwOptionFeedRecovery());
        var active = Open.AddHours(1);
        foreach (var (key, state) in groups)
        {
            state.Observe(active, active);
            if (key != (market, kind)) state.Observe(active.AddSeconds(11), active.AddSeconds(11));
        }
        Assert.DoesNotContain(groups, group => group.Value.TryStartRenewal(Open, active.AddSeconds(11)));
        var renewed = Assert.Single(groups, group => group.Value.TryStartRenewal(Open, active.AddSeconds(12)));
        Assert.Equal((market, kind), renewed.Key);
        Assert.False(renewed.Value.TryStartRenewal(Open, active.AddSeconds(13)));

        // Cached repeats cannot postpone recovery or allow a second renewal.
        renewed.Value.Observe(active, active.AddSeconds(15));
        Assert.False(renewed.Value.TryStartRenewal(Open, active.AddSeconds(45)));
        // Actual new data rearms recovery for a later interruption.
        renewed.Value.Observe(active.AddSeconds(45), active.AddSeconds(45));
        Assert.True(renewed.Value.TryStartRenewal(Open, active.AddSeconds(57)));
    }

    [Fact]
    public void GivesTheOpenAndReconnectGraceAndIgnoresStaleOrFutureFrames()
    {
        var feed = new GrowwOptionFeedRecovery();
        var subscribed = Open.AddMinutes(-15);
        Assert.False(feed.TryStartRenewal(subscribed, Open.AddSeconds(-1)));
        Assert.False(feed.TryStartRenewal(subscribed, Open.AddSeconds(11)));
        feed.Observe(Open.AddMinutes(-1), Open.AddSeconds(10));
        feed.Observe(Open.AddMinutes(1), Open.AddSeconds(10));
        Assert.True(feed.TryStartRenewal(subscribed, Open.AddSeconds(12)));
        Assert.False(new GrowwOptionFeedRecovery().TryStartRenewal(Open.AddMinutes(1), Open.AddMinutes(1).AddSeconds(11)));
        Assert.False(new GrowwOptionFeedRecovery().TryStartRenewal(subscribed, Open.AddHours(6).AddMinutes(15)));
    }
}
