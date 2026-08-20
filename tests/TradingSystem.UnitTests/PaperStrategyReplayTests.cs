using TradingSystem.Application.Execution;

namespace TradingSystem.UnitTests;

public sealed class PaperStrategyReplayTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 10, 4, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SplitsChronologicallyAndNeverAssignsPnlToRejectedEvaluations()
    {
        var trades = Enumerable.Range(0, 10).Select(index => Trade(index,
            actualPnl: index < 7 ? 100m : -100m,
            regime: index % 2 == 0 ? "WeakBullishTrend" : "LowVolatilityCompression",
            shadowPermit: index % 2 == 0)).ToArray();

        var report = PaperStrategyReplay.Analyze(trades, 42);
        var baseline = Assert.Single(report.Variants, value => value.Code == "ACTUAL_BASELINE");

        Assert.Equal(7, baseline.Training.Trades);
        Assert.Equal(3, baseline.Validation.Trades);
        Assert.Equal(42, report.RejectedEvaluationsWithoutOptionPath);
        Assert.Contains(report.Limitations, value => value.Contains("no invented P&L", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FixedOneRReplayUsesFirstObservedBoundaryAndIncludesCharges()
    {
        var report = PaperStrategyReplay.Analyze([
            Trade(0, -500m, "WeakBullishTrend", true, [100m, 111m, 90m]),
            Trade(1, -500m, "WeakBullishTrend", true, [100m, 89m, 111m])
        ], 0);
        var variant = Assert.Single(report.Variants, value => value.Code == "FIXED_1R_10_PERCENT");

        Assert.True(variant.Training.NetPnl > 0);
        Assert.True(variant.Validation.NetPnl < 0);
        Assert.True(variant.Training.NetPnl < 1_100m);
    }

    [Fact]
    public void EmptyReplayReturnsZeroMetrics()
    {
        var report = PaperStrategyReplay.Analyze([], 5);

        Assert.Equal(5, report.Variants.Count);
        Assert.All(report.Variants, value => Assert.Equal(0, value.Validation.Trades));
    }

    private static ReplayTradeInput Trade(int index, decimal actualPnl, string regime,
        bool shadowPermit, decimal[]? prices = null)
    {
        var observed = prices ?? [100m, 101m];
        return new(Guid.NewGuid(), Start.AddMinutes(index), 100m, observed[^1], 100,
            actualPnl, regime, shadowPermit, observed.Select((price, point) =>
                new ReplayPricePoint(Start.AddMinutes(index + point), price)).ToArray());
    }
}
