using TradingSystem.Application.MarketData;

namespace TradingSystem.UnitTests;

public sealed class WorkspacePreviousClosePolicyTests
{
    [Fact]
    public void UsesFridayClosingCandleOnMondayAndIgnoresAfterHours()
    {
        var friday = new DateTimeOffset(2026, 10, 30, 9, 59, 0, TimeSpan.Zero);
        var result = WorkspacePreviousClosePolicy.Read([
            Bar(friday, 100m), Bar(friday.AddMinutes(1), 999m),
            Bar(friday.AddDays(3).AddHours(-3), 105m)
        ], new DateOnly(2026, 11, 2), new TimeOnly(15, 30));
        Assert.NotNull(result);
        Assert.Equal(100m, result.Price);
        Assert.Equal(new DateOnly(2026, 10, 30), result.SessionDate);
    }

    [Fact]
    public void MissingFinalMinuteDoesNotInventAPreviousCloseOrFallBackAnotherDay()
    {
        var prior = new DateTimeOffset(2026, 11, 3, 9, 59, 0, TimeSpan.Zero);
        Assert.Null(WorkspacePreviousClosePolicy.Read([Bar(prior, 100m),
            Bar(prior.AddDays(1).AddMinutes(-1), 120m)], new DateOnly(2026, 11, 5), new TimeOnly(15, 30)));
        Assert.Null(WorkspacePreviousClosePolicy.Read([], new DateOnly(2026, 11, 5), new TimeOnly(15, 30)));
    }

    private static WorkspaceCandle Bar(DateTimeOffset at, decimal close) =>
        new(at, 60, close, close, close, close, 0, true);
}
