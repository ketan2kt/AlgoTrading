using TradingSystem.Application.Execution;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class PaperProtectiveStopPolicyTests
{
    [Theory]
    [InlineData(Direction.Buy, 90, 108, 104)]
    [InlineData(Direction.Buy, 90, 116, 110.4)]
    [InlineData(Direction.Sell, 110, 92, 96)]
    public void ProgressiveLockRetainsHalfOfPeakAfterActivation(Direction direction,
        decimal initialStop, decimal extreme, decimal expected) =>
        Assert.Equal(expected, PaperProtectiveStopPolicy.Calculate(direction,
            100m, initialStop, extreme, .5m, 1.25m, .65m, .2m));

    [Theory]
    [InlineData(Direction.Buy, 100, 95, 100, 120, 101.25)]
    [InlineData(Direction.Sell, 100, 105, 100, 120, 98.75)]
    public void CostFloorProtectsAPositiveNetExit(Direction direction, decimal entry,
        decimal currentStop, int quantity, decimal costs, decimal expected) =>
        Assert.Equal(expected, PaperProtectiveStopPolicy.ProtectNetProfit(direction,
            entry, currentStop, quantity, costs));

    [Theory]
    [InlineData(109, 90)]
    [InlineData(110, 100)]
    [InlineData(125, 115)]
    public void LongPositionMovesFromInitialToBreakEvenAndTrailing(decimal highWater, decimal expected) =>
        Assert.Equal(expected, PaperProtectiveStopPolicy.Calculate(Direction.Buy,
            100, 90, highWater, 1, 1));

    [Fact]
    public void ProtectiveStopNeverMovesBackward() =>
        Assert.Equal(115, PaperProtectiveStopPolicy.Calculate(Direction.Buy,
            100, 90, 125, 1, 1));

    [Theory]
    [InlineData(108.99, 90)]
    [InlineData(109, 108)]
    [InlineData(110, 108)]
    public void LongPositionLocksPointEightRBeforeOneRTarget(decimal highWater, decimal expected) =>
        Assert.Equal(expected, PaperProtectiveStopPolicy.Calculate(Direction.Buy,
            100, 90, highWater, 1, 1, 0.9m, 0.8m));

    [Fact]
    public void ShortPositionLocksSymmetricProfit() =>
        Assert.Equal(92, PaperProtectiveStopPolicy.Calculate(Direction.Sell,
            100, 110, 91, 1, 1, 0.9m, 0.8m));

    [Theory]
    [InlineData(1m, .50m)]
    [InlineData(1.5m, .65m)]
    [InlineData(2.5m, .75m)]
    public void PeakProfitLockStrengthensByRiskMultiple(decimal peakR, decimal lockedFraction) =>
        Assert.Equal(lockedFraction, PaperProtectiveStopPolicy.PeakProfitLockFraction(peakR));

    [Theory]
    [InlineData(Direction.Buy, 100, 110, 90, 108)]
    [InlineData(Direction.Sell, 100, 90, 110, 92)]
    public void ConfirmedReversalLocksEightyPercentOfOpenProfit(Direction direction,
        decimal entry, decimal current, decimal existingStop, decimal expected) =>
        Assert.Equal(expected, PaperProtectiveStopPolicy.ProtectReversalProfit(
            direction, entry, current, existingStop, 0.8m));
}
