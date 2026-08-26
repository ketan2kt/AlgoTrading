using TradingSystem.Application.Execution;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class PaperPositionExitPolicyTests
{
    [Theory]
    [InlineData(Direction.Buy, 99, PaperExitReason.StopLoss)]
    [InlineData(Direction.Buy, 121, PaperExitReason.Target)]
    [InlineData(Direction.Sell, 121, PaperExitReason.StopLoss)]
    [InlineData(Direction.Sell, 79, PaperExitReason.Target)]
    public void EvaluateDetectsDirectionalProtectiveExit(
        Direction direction, decimal price, PaperExitReason expected)
    {
        var result = PaperPositionExitPolicy.Evaluate(direction, price, 100m, 120m,
            new TimeOnly(12, 0), new TimeOnly(15, 15), true);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void EvaluateClosesFreshPositionAtForcedExit()
    {
        var result = PaperPositionExitPolicy.Evaluate(Direction.Buy, 110m, 100m, 120m,
            new TimeOnly(15, 15), new TimeOnly(15, 15), true);
        Assert.Equal(PaperExitReason.ForcedIntradayExit, result);
    }

    [Fact]
    public void EvaluateDoesNotFabricateExitFromStalePrice()
    {
        var result = PaperPositionExitPolicy.Evaluate(Direction.Buy, 90m, 100m, 120m,
            new TimeOnly(15, 20), new TimeOnly(15, 15), false);
        Assert.Equal(PaperExitReason.None, result);
    }

    [Fact]
    public void EvaluateLetsConfirmedRunnerContinueBeyondOriginalTarget()
    {
        var result = PaperPositionExitPolicy.Evaluate(Direction.Buy, 121m, 108m, 120m,
            new TimeOnly(12, 0), new TimeOnly(15, 15), true, targetExitEnabled: false);

        Assert.Equal(PaperExitReason.None, result);
    }

    [Fact]
    public void ForcedExitStillClosesRunnerBeyondOriginalTarget()
    {
        var result = PaperPositionExitPolicy.Evaluate(Direction.Buy, 121m, 108m, 120m,
            new TimeOnly(15, 15), new TimeOnly(15, 15), true, targetExitEnabled: false);

        Assert.Equal(PaperExitReason.ForcedIntradayExit, result);
    }
}
