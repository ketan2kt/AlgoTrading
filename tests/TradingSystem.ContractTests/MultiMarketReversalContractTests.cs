using TradingSystem.Application.Execution;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Execution;
using TradingSystem.Infrastructure.MarketData;

namespace TradingSystem.ContractTests;

public sealed class MultiMarketReversalContractTests
{
    private static readonly UnderlyingTrendInvalidationResult Confirmed =
        new(true, 1m, ["Completed opposite structure and swing break."], SessionBehaviour.Trending);

    [Theory]
    [InlineData("sensex")]
    [InlineData("bank-nifty")]
    public void ConfirmedLosingReversalExitsBeforeFullPremiumStop(string marketCode)
    {
        var result = MultiMarketPaperTradingService.ConfirmedReversalResponse(
            TradingMarketCatalog.Get(marketCode), Confirmed, Direction.Buy,
            200m, 190m, 180m, true, .80m);

        Assert.True(result.ShouldExit);
        Assert.False(result.ProtectProfit);
        Assert.Equal(180m, result.Stop);
    }

    [Theory]
    [InlineData("sensex")]
    [InlineData("bank-nifty")]
    public void ConfirmedProfitableReversalTightensStopLikeNifty(string marketCode)
    {
        var result = MultiMarketPaperTradingService.ConfirmedReversalResponse(
            TradingMarketCatalog.Get(marketCode), Confirmed, Direction.Buy,
            200m, 220m, 190m, true, .80m);

        Assert.False(result.ShouldExit);
        Assert.True(result.ProtectProfit);
        Assert.Equal(216m, result.Stop);
    }

    [Theory]
    [InlineData("sensex")]
    [InlineData("bank-nifty")]
    public void UnconfirmedPullbackDoesNotChangeExitOrStop(string marketCode)
    {
        var result = MultiMarketPaperTradingService.ConfirmedReversalResponse(
            TradingMarketCatalog.Get(marketCode), Confirmed with { ShouldExit = false },
            Direction.Buy, 200m, 190m, 180m, true, .80m);

        Assert.False(result.ShouldExit);
        Assert.False(result.ProtectProfit);
        Assert.Equal(180m, result.Stop);
    }

    [Fact]
    public void ProtectedStopUsesTheOptionContractTick()
    {
        var result = MultiMarketPaperTradingService.ConfirmedReversalResponse(
            TradingMarketCatalog.BankNifty, Confirmed, Direction.Buy,
            200m, 217.35m, 190m, true, .80m, .05m);

        Assert.False(result.ShouldExit);
        Assert.Equal(213.85m, result.Stop);
    }

    [Fact]
    public void MultiMarketRuleDoesNotChangeTheSeparateNiftyEngine()
    {
        var result = MultiMarketPaperTradingService.ConfirmedReversalResponse(
            TradingMarketCatalog.Nifty, Confirmed, Direction.Buy,
            200m, 190m, 180m, true, .80m);

        Assert.False(result.ShouldExit);
        Assert.Equal(180m, result.Stop);
    }
}
