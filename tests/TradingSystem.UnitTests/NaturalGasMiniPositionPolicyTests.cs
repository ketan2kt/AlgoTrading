using TradingSystem.Application.Execution;

namespace TradingSystem.UnitTests;

public sealed class NaturalGasMiniPositionPolicyTests
{
    [Fact]
    public void PositionIsAlwaysFourLotsOfTwoHundredFiftyUnits()
    {
        Assert.Equal(250, NaturalGasMiniPositionPolicy.ExpectedLotSize);
        Assert.Equal(4, NaturalGasMiniPositionPolicy.FixedLots);
        Assert.Equal(1000, NaturalGasMiniPositionPolicy.FixedQuantity);
        Assert.True(NaturalGasMiniPositionPolicy.IsSupportedContract(250));
        Assert.False(NaturalGasMiniPositionPolicy.IsSupportedContract(1250));
    }
}
