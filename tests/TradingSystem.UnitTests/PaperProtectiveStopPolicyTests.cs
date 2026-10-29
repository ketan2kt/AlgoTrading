using TradingSystem.Application.Execution;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class PaperProtectiveStopPolicyTests
{
    [Theory]
    [InlineData(Direction.Buy, 90, 108, 104)]
    [InlineData(Direction.Buy, 90, 116, 111.2)]
    [InlineData(Direction.Sell, 110, 92, 96)]
    public void ProgressiveLockScalesWithPeakAfterActivation(Direction direction,
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

    [Fact]
    public void PeakProfitLockStrengthensByRiskMultiple()
    {
        var cases = new (decimal PeakR, decimal LockedFraction)[]
        {
            (.89m, .50m),
            (.90m, .65m),
            (1m, .65m),
            (1.5m, .70m),
            (2.5m, .75m)
        };

        foreach (var (peakR, lockedFraction) in cases)
            Assert.Equal(lockedFraction, PaperProtectiveStopPolicy.PeakProfitLockFraction(peakR));
    }

    [Fact]
    public void NearOneRWinnerProtectsSixtyFivePercentWithoutUsingCurrencyThreshold()
    {
        // 29 October Nifty example: entry 189.70, initial risk 19.00,
        // observed peak 207.30 (0.926R). The same calculation applies to Sensex.
        var stop = PaperProtectiveStopPolicy.Calculate(Direction.Buy,
            189.70m, 170.70m, 207.30m, .50m, 1.25m, .65m, .20m);

        Assert.Equal(201.14m, stop);
    }

    [Theory]
    [InlineData(Direction.Buy, 100, 110, 90, 108)]
    [InlineData(Direction.Sell, 100, 90, 110, 92)]
    public void ConfirmedReversalLocksEightyPercentOfOpenProfit(Direction direction,
        decimal entry, decimal current, decimal existingStop, decimal expected) =>
        Assert.Equal(expected, PaperProtectiveStopPolicy.ProtectReversalProfit(
            direction, entry, current, existingStop, 0.8m));
}
