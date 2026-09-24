using TradingSystem.Application.Execution;
using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class SensexSetupLifecyclePolicyTests
{
    [Fact]
    public void RejectsMatureEmergingSetupAsChampionEntry()
    {
        var lifecycle = new SensexSetupLifecycle(SensexSetupPhase.Emerging, Direction.Buy,
            8, .25m, .40m, []);

        Assert.False(SensexSetupLifecyclePolicy.AllowsChampionEntry(lifecycle));
    }

    [Fact]
    public void AllowsFreshEntryWindow()
    {
        var lifecycle = new SensexSetupLifecycle(SensexSetupPhase.EntryWindow, Direction.Sell,
            5, .20m, .30m, []);

        Assert.True(SensexSetupLifecyclePolicy.AllowsChampionEntry(lifecycle));
    }

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
