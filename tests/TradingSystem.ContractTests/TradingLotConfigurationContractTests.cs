using TradingSystem.Application.Execution;

namespace TradingSystem.ContractTests;

public sealed class TradingLotConfigurationContractTests
{
    [Fact]
    public void DefaultsBothMarketsToTenLots()
    {
        Assert.Equal(10, TradingLotConfiguration.Default.NiftyLots);
        Assert.Equal(10, TradingLotConfiguration.Default.SensexLots);
        Assert.Equal(10, TradingLotConfiguration.Default.BankNiftyLots);
        Assert.Equal(10, TradingLotConfiguration.Default.BankexLots);
    }

    [Theory]
    [InlineData("nifty", 12)]
    [InlineData("sensex", 17)]
    [InlineData("SENSEX", 17)]
    [InlineData("bank-nifty", 19)]
    [InlineData("bankex", 23)]
    public void ResolvesMarketSpecificLots(string market, int expected)
    {
        var configuration = new TradingLotConfiguration(12, 17, null, 19, 23);

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
