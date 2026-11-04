using TradingSystem.Application.Execution;

namespace TradingSystem.UnitTests;

public sealed class HistoricalBarAggregationTests
{
    [Fact]
    public void RequiresAllFiveMinutesAndUsesLastCloseTime()
    {
        var start = new DateTimeOffset(2026, 10, 29, 3, 45, 0, TimeSpan.Zero);
        var full = Enumerable.Range(0, 5).Select(index =>
            new HistoricalReplayBar(start.AddMinutes(index), 60,
                100m + index, 102m + index, 99m + index, 101m + index)
            { Symbol = "NIFTY", Volume = 10, OpenInterest = 100m + index })
            .ToArray();
        var bar = Assert.Single(HistoricalBarAggregation.ToFiveMinutes(full));
        Assert.Equal(start.AddMinutes(5), bar.AvailableAtUtc);
        Assert.Equal(100m, bar.Open);
        Assert.Equal(105m, bar.Close);
        Assert.Equal(106m, bar.High);
        Assert.Equal(99m, bar.Low);
        Assert.Equal(50, bar.Volume);
        Assert.Equal(104m, bar.OpenInterest);
        Assert.Empty(HistoricalBarAggregation.ToFiveMinutes(full.Where((_, index) =>
            index != 2)));
    }
}
