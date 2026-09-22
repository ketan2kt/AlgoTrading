using TradingSystem.Application.MarketData;

namespace TradingSystem.UnitTests;

public sealed class WorkspaceTelemetryCalculatorTests
{
    [Fact]
    public void CalculatesPointInTimeSensexTelemetryWithoutUsingFutureBars()
    {
        var start = new DateTimeOffset(2026, 9, 22, 3, 45, 0, TimeSpan.Zero);
        var underlying = Enumerable.Range(0, 25).Select(i => Bar(start.AddMinutes(i),
            75000 + i * 10, 0)).ToArray();
        var futures = Enumerable.Range(0, 25).Select(i => Bar(start.AddMinutes(i),
            75010 + i * 10, 100 + i * 10)).ToArray();

        var result = WorkspaceTelemetryCalculator.At(underlying, futures,
            start.AddMinutes(20), start);

        Assert.Equal(75200m, result.CurrentPrice);
        Assert.Equal(75145m, result.OpeningRangeHigh);
        Assert.Equal(74995m, result.OpeningRangeLow);
        Assert.True(result.Vwap > 75000m);
        Assert.True(result.FastEma > result.SlowEma);
        Assert.True(result.AtrPercent > 0);
        Assert.True(result.RelativeFuturesVolume > 1m);
    }

    private static WorkspaceCandle Bar(DateTimeOffset time, decimal close, long volume) =>
        new(time, 60, close - 2, close + 5, close - 5, close, volume, true);
}
