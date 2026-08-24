using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class IndexMomentumBreakoutPolicyTests
{
    [Fact]
    public void ConfirmsPersistentBearishBreak()
    {
        var candles = Trend(120m, -0.35m, 20).ToList();
        candles.Add(Bar(113m, 113.1m, 111.8m, 112m));

        var result = IndexMomentumBreakoutPolicy.Evaluate(candles);

        Assert.Equal(Direction.Sell, result.Direction);
        Assert.True(result.Confidence >= 0.50m);
    }

    [Fact]
    public void RejectsSingleCandleSpikeWithoutPersistence()
    {
        var candles = Trend(100m, 0.05m, 20).ToList();
        candles[^2] = Bar(100.9m, 101m, 100.5m, 100.6m);
        candles.Add(Bar(100.6m, 100.7m, 98.8m, 99m));

        var result = IndexMomentumBreakoutPolicy.Evaluate(candles);

        Assert.Null(result.Direction);
    }

    [Fact]
    public void RejectsExhaustedOversizedBreakoutCandle()
    {
        var candles = Trend(120m, -0.15m, 20).ToList();
        candles.Add(Bar(117.1m, 117.2m, 110m, 110.1m));

        var result = IndexMomentumBreakoutPolicy.Evaluate(candles);

        Assert.Null(result.Direction);
    }

    private static IEnumerable<Candle> Trend(decimal start, decimal step, int count)
    {
        for (var index = 0; index < count; index++)
        {
            var open = start + step * index;
            var close = open + step * 0.7m;
            yield return Bar(open, Math.Max(open, close) + 0.15m,
                Math.Min(open, close) - 0.15m, close);
        }
    }

    private static Candle Bar(decimal open, decimal high, decimal low, decimal close) =>
        new(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, 60,
            open, high, low, close, 100, "Groww", null);
}
