using TradingSystem.Application.Broker;
using TradingSystem.Infrastructure.Execution;

namespace TradingSystem.ContractTests;

public sealed class LiveEntryQuoteVerifierTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 25, 4, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("Groww")]
    [InlineData("Upstox")]
    public void BothFeedsMeetTheSamePriceDepthAndThirtySecondRules(string source)
    {
        var quote = new GrowwQuote(100, At.ToUnixTimeMilliseconds(), 99, 10, 101, 20, 100, 1, null) { Source = source };
        Assert.True(LiveEntryQuoteVerifier.Evaluate(quote, At.AddSeconds(30)).Allowed);
        Assert.Equal(source, LiveEntryQuoteVerifier.Evaluate(quote, At).Source);
        Assert.False(LiveEntryQuoteVerifier.Evaluate(quote, At.AddSeconds(31)).Allowed);
        Assert.False(LiveEntryQuoteVerifier.Evaluate(quote, At.AddSeconds(-1)).Allowed);
        Assert.False(LiveEntryQuoteVerifier.Evaluate(quote with { Source = "Unknown" }, At).Allowed);
        Assert.False(LiveEntryQuoteVerifier.Evaluate(quote with { BidQuantity = 0 }, At).Allowed);
        Assert.False(LiveEntryQuoteVerifier.Evaluate(quote with { OfferQuantity = null }, At).Allowed);
        Assert.False(LiveEntryQuoteVerifier.Evaluate(quote with { OfferPrice = 98 }, At).Allowed);
        Assert.False(LiveEntryQuoteVerifier.Evaluate(quote with { LastPrice = 0 }, At).Allowed);
        Assert.False(LiveEntryQuoteVerifier.Evaluate(quote with { LastTradeTimeEpochMilliseconds = null, ObservedAtUtc = null }, At).Allowed);
    }

    [Fact]
    public void DisplayReceiptsAreMarketAndBrokerSpecificAndExpireRatherThanClaimingHealthy()
    {
        var state = new LiveEntryReadiness();
        state.Report("sensex", "Groww", "Blocked", "Broker could not reconcile.", At);
        state.Report("NIFTY", "Groww", "Monitoring", "Watching setups.", At);
        state.Report("BANKNIFTY", "Groww", "Monitoring", "Watching setups.", At);
        Assert.Equal("Blocked", state.Read("SENSEX", "Groww", At).Status);
        Assert.Equal("Monitoring", state.Read("BANK-NIFTY", "Groww", At).Status);
        Assert.Equal("Monitoring", state.Read("NIFTY", "Groww", At).Status);
        Assert.Equal("Unknown", state.Read("SENSEX", "Upstox", At).Status);
        Assert.Equal("Unknown", state.Read("NIFTY", "Groww", At.AddSeconds(31)).Status);
        Assert.Equal("Unknown", state.Read("NIFTY", "Groww", At.AddSeconds(-1)).Status);
        state.BlockBroker("Groww", "Account unavailable.", At.AddSeconds(1));
        Assert.Equal("Blocked", state.Read("NIFTY", "Groww", At.AddSeconds(1)).Status);
        state.Report("NIFTY", "Groww", "Monitoring", "Recovered.", At.AddSeconds(2));
        Assert.Equal("Monitoring", state.Read("NIFTY", "Groww", At.AddSeconds(2)).Status);
        Assert.Equal("Blocked", state.Read("SENSEX", "Groww", At.AddSeconds(2)).Status);
    }
}
