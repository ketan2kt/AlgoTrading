using TradingSystem.Application.Execution;
using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class StructuralReentryPolicyTests
{
    [Fact]
    public void RejectsImmediateSameDirectionReentryWithoutNewCandles()
    {
        var bars = Bars([100m, 101m, 102m, 103m, 104m, 105m, 106m, 107m, 108m, 109m]);
        var exit = bars[^1].OpenTimeUtc.AddSeconds(-30);

        var result = StructuralReentryPolicy.Evaluate(bars, Direction.Buy, exit);

        Assert.False(result.Permitted);
        Assert.False(result.NewStructureObserved);
    }

    [Fact]
    public void AllowsReentryAfterCompletedPullbackAndBullishRejection()
    {
        var bars = Bars([100m, 101m, 102m, 103m, 104m, 105m, 106m, 107m, 108m,
            106m, 105m, 107m]);
        var exit = bars[8].OpenTimeUtc;

        var result = StructuralReentryPolicy.Evaluate(bars, Direction.Buy, exit);

        Assert.True(result.Permitted);
        Assert.True(result.NewStructureObserved);
    }

    private static StrategyPriceBar[] Bars(decimal[] closes)
    {
        var start = new DateTimeOffset(2026, 9, 23, 4, 0, 0, TimeSpan.Zero);
        return closes.Select((close, i) => new StrategyPriceBar(start.AddMinutes(i),
            i == closes.Length - 1 ? close - 1 : close, close + 1, close - 1, close)).ToArray();
    }
}
