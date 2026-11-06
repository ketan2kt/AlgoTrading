using TradingSystem.Application.Execution;

namespace TradingSystem.UnitTests;

public sealed class OptionRiskBudgetPolicyTests
{
    [Theory]
    [InlineData(756.55, 680.90, 20, 5, "BSE", 100)]
    [InlineData(696.35, 626.70, 20, 5, "BSE", 100)]
    [InlineData(727.80, 655.00, 30, 2, "NSE", 60)]
    [InlineData(132.50, 119.25, 65, 5, "NSE", 325)]
    public void RecordedEntryGeometryUsesSelectedWholeLots(decimal entry, decimal stop,
        int lot, int lots, string exchange, int expected)
    {
        var result = OptionRiskBudgetPolicy.Size(entry, stop, lot, lots, exchange);
        Assert.Equal(expected, result.Quantity);
        Assert.Equal(result.PlannedLoss, result.Budget);
        Assert.True(result.EstimatedCosts > 0m);
        Assert.Equal((entry - stop) * expected + result.EstimatedCosts, result.PlannedLoss);
    }

    [Fact]
    public void LargeRiskIsReportedNotSilentlyCappedButInvalidStopsAreRejected()
    {
        var result = OptionRiskBudgetPolicy.Size(1000, 900, 50, 1, "NSE");
        Assert.Equal(50, result.Quantity);
        Assert.True(result.PlannedLoss > 5000m);
        Assert.Equal(0, OptionRiskBudgetPolicy.Size(100, 100, 50, 5, "NSE").Quantity);
    }

    [Fact]
    public void RiskScalesWithLotsAndPremiumWithoutIncreasingSavedSize()
    {
        Assert.Equal(20, OptionRiskBudgetPolicy.Size(100, 90, 20, 1, "BSE").Quantity);
        var selected = OptionRiskBudgetPolicy.Size(1000, 900, 20, 5, "BSE");
        Assert.Equal(100, selected.Quantity);
        Assert.Equal(10000m + selected.EstimatedCosts, selected.PlannedLoss);
        Assert.Equal(0, OptionRiskBudgetPolicy.Size(100, 90, int.MaxValue, 2, "BSE").Quantity);
    }
}
