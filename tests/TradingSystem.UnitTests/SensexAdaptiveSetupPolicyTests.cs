using TradingSystem.Application.Execution;
using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class SensexAdaptiveSetupPolicyTests
{
    [Fact]
    public void DirectionalUnextendedMoveIsPreferredAndPermitted()
    {
        var start = DateTimeOffset.UnixEpoch;
        var bars = Enumerable.Range(0, 14).Select(i => new StrategyPriceBar(start.AddMinutes(i),
            100+i, 101+i, 99+i, 100+i)).ToArray();
        var result = SensexAdaptiveSetupPolicy.Assess(bars, Direction.Buy, 2, false);
        Assert.False(result.ShadowOnly);
        Assert.Equal(SensexMarketState.DirectionalTrend, result.State);
        Assert.Equal(SensexSetupVerdict.Prefer, result.Verdict);
        Assert.True(SensexAdaptiveSetupPolicy.AllowsMomentumEntry(result));
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
        Assert.False(SensexAdaptiveSetupPolicy.AllowsMomentumEntry(result));
    }

    [Fact]
    public void InsufficientDataIsUnknownAndNeverInvented()
    {
        var result = SensexAdaptiveSetupPolicy.Assess([], Direction.Buy, 0, false);
        Assert.Equal(SensexSetupVerdict.Observe, result.Verdict);
        Assert.NotEmpty(result.Limitations);
        Assert.False(SensexAdaptiveSetupPolicy.AllowsMomentumEntry(result));
    }

    [Fact]
    public void RangeObservationCannotOpenMomentumTrade()
    {
        var result = new SensexAdaptiveSetupAssessment("test", false,
            SensexMarketState.StructuredRange, SensexSetupVerdict.Observe,
            "BoundaryReactionOnly", 0.20m, 0.75m, 0.45m, 0.06m, 0.60m,
            0.10m, 0.15m, 0.80m, [],
            ["Momentum continuation is mismatched with the current range hypothesis."], []);

        Assert.False(SensexAdaptiveSetupPolicy.AllowsMomentumEntry(result));
    }

    [Fact]
    public void CleanEmergingTrendCanOpenBeforeClassifierFullyTransitions()
    {
        var result = new SensexAdaptiveSetupAssessment("test", false,
            SensexMarketState.Transition, SensexSetupVerdict.Observe,
            "WaitForAcceptance", .55m, .45m, .90m, .40m, .30m,
            .75m, .60m, null, ["Direction is aligned with EMA 9/21."],
            ["Market state is unstable; acceptance or a boundary reaction is not confirmed."], []);

        Assert.True(SensexAdaptiveSetupPolicy.AllowsMomentumEntry(result));
    }

    [Theory]
    [InlineData(.31, .75, .60)]
    [InlineData(.40, .24, .60)]
    [InlineData(.40, .75, .81)]
    public void WeakOrExtendedTransitionRemainsBlocked(double efficiency, double separation,
        double extension)
    {
        var result = new SensexAdaptiveSetupAssessment("test", false,
            SensexMarketState.Transition, SensexSetupVerdict.Observe,
            "WaitForAcceptance", .50m, .45m, .95m, (decimal)efficiency, .30m,
            (decimal)separation, (decimal)extension, null, [], ["Transition."], []);

        Assert.False(SensexAdaptiveSetupPolicy.AllowsMomentumEntry(result));
    }
}
