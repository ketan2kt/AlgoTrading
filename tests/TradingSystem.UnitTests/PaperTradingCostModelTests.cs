using TradingSystem.Application.Execution;

namespace TradingSystem.UnitTests;

public sealed class PaperTradingCostModelTests
{
    [Fact]
    public void EstimateRoundTripCostUsesBothTurnoverLegs()
    {
        var result = PaperTradingCostModel.EstimateRoundTripCost(20_000m, 20_100m, 10, 5m);
        Assert.Equal(200.5m, result);
    }

    [Fact]
    public void EstimateRoundTripCostAllowsExplicitZeroCostReplay()
    {
        var result = PaperTradingCostModel.EstimateRoundTripCost(100m, 110m, 2, 0m);
        Assert.Equal(0m, result);
    }
}
