using TradingSystem.Application.MarketData;

namespace TradingSystem.UnitTests;

public sealed class InstrumentPriceIncrementTests
{
    [Theory]
    [InlineData(10, 0.10)]
    [InlineData(5, 0.05)]
    [InlineData(0.05, 0.05)]
    public void NormalizesGrowwInstrumentMasterUnits(decimal value, decimal expected) =>
        Assert.Equal(expected, InstrumentPriceIncrement.FromGrowwInstrumentMaster(value));
}
