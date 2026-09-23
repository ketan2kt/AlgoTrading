using TradingSystem.Application.Execution;
using TradingSystem.Application.Strategies;

namespace TradingSystem.UnitTests;

public sealed class SensexSetupLifecyclePolicyTests
{
    [Fact]
    public void MarksLateBreakdownAsExtended()
    {
        var start = new DateTimeOffset(2026, 9, 23, 4, 0, 0, TimeSpan.Zero);
        var closes = Enumerable.Range(0, 20).Select(i => 800m - i * 2m).ToArray();
        closes[^1] -= 12m;
        var bars = closes.Select((close, i) => new StrategyPriceBar(start.AddMinutes(i),
            close + 1, close + 2, close - 2, close)).ToArray();

        var result = SensexSetupLifecyclePolicy.Evaluate(bars);

        Assert.Equal(SensexSetupPhase.Extended, result.Phase);
        Assert.True(result.AtrExtension > .65m);
    }

    [Fact]
    public void ReportsUnavailableBeforeMinimumEvidence()
    {
        var result = SensexSetupLifecyclePolicy.Evaluate([]);

        Assert.Equal(SensexSetupPhase.Unavailable, result.Phase);
    }
}
