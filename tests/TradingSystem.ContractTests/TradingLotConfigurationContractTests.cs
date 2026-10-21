using TradingSystem.Application.Execution;

namespace TradingSystem.ContractTests;

public sealed class TradingLotConfigurationContractTests
{
    [Fact]
    public void DefaultsBothMarketsToTenLots()
    {
        Assert.Equal(10, TradingLotConfiguration.Default.NiftyLots);
        Assert.Equal(10, TradingLotConfiguration.Default.SensexLots);
    }

    [Theory]
    [InlineData("nifty", 12)]
    [InlineData("sensex", 17)]
    [InlineData("SENSEX", 17)]
    public void ResolvesMarketSpecificLots(string market, int expected)
    {
        var configuration = new TradingLotConfiguration(12, 17);

        Assert.Equal(expected, configuration.ForMarket(market));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(10, true)]
    [InlineData(50, true)]
    [InlineData(51, false)]
    public void EnforcesSupportedLotRange(int lots, bool expected) =>
        Assert.Equal(expected, TradingLotConfiguration.IsValid(lots));
}
