using TradingSystem.Application.Execution;
using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class FirstEntryTimingPolicyTests
{
    [Fact]
    public void RejectsEntryAfterExtendedBreakout()
    {
        var bars = Bars(Enumerable.Range(0, 15).Select(i => 100m + i * .1m).Append(104m).ToArray());

        var result = FirstEntryTimingPolicy.Evaluate(bars, Direction.Buy);

        Assert.False(result.Permitted);
        Assert.True(result.AtrExtension > .45m);
    }

    [Fact]
    public void AllowsEntryNearTriggerWithoutOversizedImpulse()
    {
        var closes = Enumerable.Range(0, 16).Select(i => 100m + i * .10m).ToArray();
        var bars = Bars(closes);

        var result = FirstEntryTimingPolicy.Evaluate(bars, Direction.Buy);

        Assert.True(result.Permitted);
    }

    private static StrategyPriceBar[] Bars(decimal[] closes)
    {
        var start = new DateTimeOffset(2026, 9, 24, 3, 45, 0, TimeSpan.Zero);
        return closes.Select((close, i) => new StrategyPriceBar(start.AddMinutes(i * 5),
            close - .05m, close + .15m, close - .15m, close)).ToArray();
    }
}
