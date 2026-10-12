using TradingSystem.Application.Execution;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class ScalpTradingPolicyTests
{
    [Fact]
    public void SelectsAConfirmedBearishBreakdownInsteadOfTrendAlignmentAlone()
    {
        var start = new DateTimeOffset(2026, 10, 12, 3, 45, 0, TimeSpan.Zero);
        var bars = Enumerable.Range(0, 18).Select(index =>
        {
            var close = 25000m - index * 2m;
            return new ScalpPriceBar(start.AddMinutes(index * 5), close + 1m, close + 3m,
                close - 2m, close, 1000 + index * 10);
        }).ToList();
        bars[^1] = new(start.AddMinutes(85), 24967m, 24968m, 24963m, 24964m, 2000);

        var result = ScalpTradingPolicy.Evaluate(bars);

        Assert.Equal(Direction.Sell, result.Direction);
        Assert.True(result.Confidence >= .70m);
        Assert.Equal("StructuredBreakout", result.Regime);
    }

    [Fact]
    public void RejectsAPlainEmaTrendWithoutBreakoutOrPullbackConfirmation()
    {
        var start = new DateTimeOffset(2026, 10, 12, 3, 45, 0, TimeSpan.Zero);
        var bars = Enumerable.Range(0, 18).Select(index =>
            new ScalpPriceBar(start.AddMinutes(index * 5), 100m + index, 102m + index,
                99m + index, 101m + index, 1000)).ToArray();

        var result = ScalpTradingPolicy.Evaluate(bars);

        Assert.Null(result.Direction);
        Assert.Equal("NoStructuredSetup", result.Regime);
    }

    [Fact]
    public void AggregatesOnlyCompleteFiveMinuteCandles()
    {
        var start = new DateTimeOffset(2026, 10, 12, 3, 45, 0, TimeSpan.Zero);
        var bars = Enumerable.Range(0, 12).Select(index =>
            new ScalpPriceBar(start.AddMinutes(index), 100m + index, 102m + index,
                99m + index, 101m + index, 100 + index)).ToArray();

        var result = ScalpTradingPolicy.AggregateCompletedBars(bars, 5);

        Assert.Equal(2, result.Count);
        Assert.Equal(start, result[0].TimeUtc);
        Assert.Equal(100m, result[0].Open);
        Assert.Equal(105m, result[0].Close);
        Assert.Equal(510, result[0].Volume);
    }

    [Fact]
    public void RejectsAnAlreadyExtendedMoveInsteadOfChasingIt()
    {
        var start = new DateTimeOffset(2026, 10, 12, 3, 45, 0, TimeSpan.Zero);
        var bars = Enumerable.Range(0, 17).Select(index =>
            new ScalpPriceBar(start.AddMinutes(index), 100m + index, 102m + index,
                99m + index, 101m + index, 1000)).ToList();
        bars.Add(new(start.AddMinutes(17), 117m, 145m, 116m, 142m, 3000));

        var result = ScalpTradingPolicy.Evaluate(bars);

        Assert.Null(result.Direction);
        Assert.Equal("Extended", result.Regime);
    }

    [Fact]
    public void RunnerStopLocksNetProfitAndNeverMovesBackward()
    {
        Assert.Equal(105m, ScalpTradingPolicy.TrailingStop(100m, 95m, 107m, 4m, 2m, 101m));
        Assert.Equal(105m, ScalpTradingPolicy.TrailingStop(100m, 105m, 106m, 4m, 2m, 101m));
        Assert.Equal(95m, ScalpTradingPolicy.TrailingStop(100m, 95m, 103m, 4m, 2m, 101m));
    }
}
