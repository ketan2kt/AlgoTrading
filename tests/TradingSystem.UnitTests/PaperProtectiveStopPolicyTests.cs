using TradingSystem.Application.Execution;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class PaperProtectiveStopPolicyTests
{
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
}
