using TradingSystem.Application.Broker;
using TradingSystem.Application.Execution;

namespace TradingSystem.UnitTests;

public sealed class OptionPaperTradePricingTests
{
    [Fact]
    public void UsesOfferForEntryAndBidForExitAfterLiquidityValidation()
    {
        var result = OptionPaperTradePricing.Validate(
            Quote(100m, 100.5m, 1000m, 5000m), 1m, 500m, 100m, 100m);

        Assert.True(result.Approved);
        Assert.Equal(100.5m, result.EntryPrice);
        Assert.Equal(100m, result.ExitReferencePrice);
    }

    [Theory]
    [InlineData(90, 110, 1000, 5000)]
    [InlineData(100, 100.5, 0, 5000)]
    [InlineData(100, 100.5, 1000, 0)]
    public void RejectsWideOrIlliquidQuotes(decimal bid, decimal offer, decimal volume, decimal oi)
    {
        var result = OptionPaperTradePricing.Validate(
            Quote(bid, offer, volume, oi), 1m, 500m, 100m, 100m);

        Assert.False(result.Approved);
        Assert.NotEmpty(result.RejectionReasons);
    }

    [Fact]
    public void ProtectivePricesAreRoundedToOptionTick()
    {
        var result = OptionPaperTradePricing.ProtectivePrices(101.03m, 10m, 2m, 0.05m);

        Assert.Equal(90.90m, result.StopLoss);
        Assert.Equal(121.25m, result.Target);
    }

    private static GrowwQuote Quote(decimal bid, decimal offer, decimal volume, decimal oi) =>
        new((bid + offer) / 2m, null, bid, 10, offer, 10, volume, oi, 0m);
}
