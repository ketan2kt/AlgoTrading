using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class SelectiveHedgePolicyTests
{
    [Fact]
    public void ApprovesOppositeLegOnlyDuringQualifiedExpansion()
    {
        var result = SelectiveHedgePolicy.Evaluate(new(true, false, 0.52m, 1.30m, 0.68m,
            MarketRegime.HighVolatilityExpansion), 0.35m, 1m, 0.60m);
        Assert.True(result.Approved);
    }

    [Fact]
    public void RejectsRoutineRangeHedgeAndDuplicateHedge()
    {
        var result = SelectiveHedgePolicy.Evaluate(new(true, true, 0.22m, 0.80m, 0.55m,
            MarketRegime.RangeBound), 0.35m, 1m, 0.60m);
        Assert.False(result.Approved);
        Assert.Equal(5, result.Reasons.Count);
    }

    [Fact]
    public void CarryForwardRequiresProfitableLongWeeklyOption()
    {
        var result = WeeklyCarryPolicy.Evaluate(new(new DateOnly(2026, 8, 14),
            new DateOnly(2026, 8, 18), Direction.Buy, 100m, 116m, 0.72m, false, false),
            7, 0.65m);
        Assert.True(result.Approved);
    }

    [Theory]
    [InlineData(Direction.Sell, 116, 0.72)]
    [InlineData(Direction.Buy, 90, 0.72)]
    [InlineData(Direction.Buy, 116, 0.50)]
    public void CarryForwardRejectsUndefinedOrWeakOvernightRisk(Direction direction,
        decimal currentPrice, decimal confidence)
    {
        var result = WeeklyCarryPolicy.Evaluate(new(new DateOnly(2026, 8, 14),
            new DateOnly(2026, 8, 18), direction, 100m, currentPrice, confidence, false, false),
            7, 0.65m);
        Assert.False(result.Approved);
    }
}
