using TradingSystem.Application.Execution;

namespace TradingSystem.UnitTests;

public sealed class IndexEntryWindowPolicyTests
{
    [Theory]
    [InlineData(9, 14, 59, false)]
    [InlineData(9, 15, 0, true)]
    [InlineData(14, 59, 59, true)]
    [InlineData(15, 0, 0, false)]
    [InlineData(15, 15, 0, false)]
    [InlineData(15, 24, 3, false)]
    public void EnforcesThreePmEvenWithOlderConfiguration(int hour, int minute, int second, bool expected)
    {
        Assert.Equal(expected, IndexEntryWindowPolicy.AllowsEntry(
            new TimeOnly(hour, minute, second), new TimeOnly(9, 15), new TimeOnly(15, 30)));
    }

    [Fact]
    public void RespectsEarlierConfiguredCutoff()
    {
        Assert.False(IndexEntryWindowPolicy.AllowsEntry(
            new TimeOnly(14, 30), new TimeOnly(9, 15), new TimeOnly(14, 30)));
    }
}
