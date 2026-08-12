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
}
