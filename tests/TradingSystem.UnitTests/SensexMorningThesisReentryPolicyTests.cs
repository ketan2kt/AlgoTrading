using TradingSystem.Application.Execution;

namespace TradingSystem.UnitTests;

public sealed class SensexMorningThesisReentryPolicyTests
{
    private static readonly DateTimeOffset PriorExit = new(2026, 10, 28, 4, 3, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(9, 30, true, false, true)]
    [InlineData(9, 42, true, true, false)]
    [InlineData(10, 44, true, true, false)]
    [InlineData(10, 45, true, true, true)]
    [InlineData(9, 42, false, true, true)]
    public void OnlyMorningPersistentThesisReentriesAreDeferred(int hour, int minute,
        bool persistentThesis, bool hasPriorExit, bool expected)
    {
        Assert.Equal(expected, SensexMorningThesisReentryPolicy.AllowsEntry(
            new TimeOnly(hour, minute), persistentThesis,
            hasPriorExit ? PriorExit : null));
    }
}
