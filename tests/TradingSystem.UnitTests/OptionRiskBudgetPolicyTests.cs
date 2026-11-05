using TradingSystem.Application.Execution;

namespace TradingSystem.UnitTests;

public sealed class OptionRiskBudgetPolicyTests
{
    [Theory]
    [InlineData(756.55, 680.90, 20, 5, "BSE", 60)]
    [InlineData(696.35, 626.70, 20, 5, "BSE", 60)]
    [InlineData(727.80, 655.00, 30, 2, "NSE", 60)]
    [InlineData(132.50, 119.25, 65, 5, "NSE", 325)]
    public void RecordedEntryGeometryUsesWholeLotsUnderBudget(decimal entry, decimal stop,
        int lot, int lots, string exchange, int expected)
    {
        var result = OptionRiskBudgetPolicy.Size(entry, stop, lot, lots, exchange);
        Assert.Equal(expected, result.Quantity);
        Assert.True(result.PlannedLoss <= 5000m);
        Assert.True(result.EstimatedCosts > 0m);
        Assert.Equal((entry - stop) * expected + result.EstimatedCosts, result.PlannedLoss);
    }

    [Fact]
    public void FeesCanMakeAnOtherwiseAffordableLotTooLarge()
    {
        Assert.Equal(0, OptionRiskBudgetPolicy.Size(1000, 900, 50, 1, "NSE").Quantity);
        Assert.Equal(0, OptionRiskBudgetPolicy.Size(100, 100, 50, 5, "NSE").Quantity);
    }

    [Fact]
    public void CannotIncreaseSavedSizeOrRelaxTheBudget()
    {
        Assert.Equal(20, OptionRiskBudgetPolicy.Size(100, 90, 20, 1, "BSE").Quantity);
        var capped = OptionRiskBudgetPolicy.Size(1000, 900, 20, 50, "BSE", 999999m);
        Assert.Equal(40, capped.Quantity);
        Assert.True(capped.PlannedLoss <= 5000m);
    }
}
