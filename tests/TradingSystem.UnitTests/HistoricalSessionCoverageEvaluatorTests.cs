using TradingSystem.Application.Execution;

namespace TradingSystem.UnitTests;

public sealed class HistoricalSessionCoverageEvaluatorTests
{
    private static readonly DateOnly Date = new(2026, 10, 29);
    private static readonly DateTimeOffset Start = new(2026, 10, 29, 3, 45, 0,
        TimeSpan.Zero);

    [Fact]
    public void CompleteSessionRequiresEveryMinuteNotJustAHighCount()
    {
        var all = Enumerable.Range(0, 375).Select(offset => Start.AddMinutes(offset));
        var coverage = HistoricalSessionCoverageEvaluator.Evaluate(Date, all);

        Assert.True(coverage.IsComplete);
        Assert.Equal(375, coverage.PresentMinutes);
        Assert.Equal(0, coverage.LongestMissingRunMinutes);
    }

    [Fact]
    public void MissingMiddleBlockIsReportedEvenWithThreeHundredSixtyBars()
    {
        var missing = Enumerable.Range(0, 375).Where(value => value is < 120 or >= 135)
            .Select(offset => Start.AddMinutes(offset));
        var coverage = HistoricalSessionCoverageEvaluator.Evaluate(Date, missing);

        Assert.False(coverage.IsComplete);
        Assert.Equal(15, coverage.MissingMinutes);
        Assert.Equal(15, coverage.LongestMissingRunMinutes);
    }
}
