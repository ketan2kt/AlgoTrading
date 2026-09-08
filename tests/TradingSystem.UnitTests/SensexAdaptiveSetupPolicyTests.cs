using TradingSystem.Application.Execution;
using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class SensexAdaptiveSetupPolicyTests
{
    [Fact]
    public void DirectionalUnextendedMoveIsPreferredInShadowMode()
    {
        var start = DateTimeOffset.UnixEpoch;
        var bars = Enumerable.Range(0, 14).Select(i => new StrategyPriceBar(start.AddMinutes(i),
            100+i, 101+i, 99+i, 100+i)).ToArray();
        var result = SensexAdaptiveSetupPolicy.Assess(bars, Direction.Buy, 2, false);
        Assert.True(result.ShadowOnly);
        Assert.Equal(SensexMarketState.DirectionalTrend, result.State);
        Assert.Equal(SensexSetupVerdict.Prefer, result.Verdict);
    }

    [Fact]
    public void ChoppyMoveDoesNotReceivePreferredVerdict()
    {
        var start = DateTimeOffset.UnixEpoch;
        var closes = new decimal[] {100,102,99,103,98,102,99,103,98,102,99,103};
        var bars = closes.Select((x,i) => new StrategyPriceBar(start.AddMinutes(i), x, x+1, x-1, x)).ToArray();
        var result = SensexAdaptiveSetupPolicy.Assess(bars, Direction.Buy, 3, false);
        Assert.NotEqual(SensexSetupVerdict.Prefer, result.Verdict);
        Assert.NotEmpty(result.Concerns);
    }

    [Fact]
    public void InsufficientDataIsUnknownAndNeverInvented()
    {
        var result = SensexAdaptiveSetupPolicy.Assess([], Direction.Buy, 0, false);
        Assert.Equal(SensexSetupVerdict.Observe, result.Verdict);
        Assert.NotEmpty(result.Limitations);
    }
}
