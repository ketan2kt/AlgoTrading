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

    [Fact]
    public void StrongMorningSetupRemainsEligible()
    {
        var result = PreferredTrend();

        var decision = SensexAdaptiveSetupPolicy.EvaluateEvidenceGate(result,
            new TimeOnly(11, 30), 1, .58m);

        Assert.True(decision.Permitted);
        Assert.Empty(decision.Reasons);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    public void EvidenceBackedWeakLateExpirySegmentsAreRejected(int daysToExpiry)
    {
        var decision = SensexAdaptiveSetupPolicy.EvaluateEvidenceGate(PreferredTrend(),
            new TimeOnly(13, 10), daysToExpiry, .70m);

        Assert.False(decision.Permitted);
        Assert.Contains(decision.Reasons, value => value.Contains("negative expectancy"));
    }

    [Fact]
    public void ExceptionalLateTwoDaySetupRemainsEligible()
    {
        var decision = SensexAdaptiveSetupPolicy.EvaluateEvidenceGate(PreferredTrend(),
            new TimeOnly(13, 10), 2, .70m);

        Assert.True(decision.Permitted);
    }

    [Fact]
    public void LateTransitionIsRejectedEvenWhenBaseTransitionRulePermitsIt()
    {
        var result = new SensexAdaptiveSetupAssessment("test", false,
            SensexMarketState.Transition, SensexSetupVerdict.Observe,
            "WaitForAcceptance", .55m, .45m, .90m, .50m, .30m,
            .75m, .60m, null, ["Direction is aligned with EMA 9/21."],
            ["Transition."], []);

        Assert.True(SensexAdaptiveSetupPolicy.AllowsMomentumEntry(result));
        Assert.False(SensexAdaptiveSetupPolicy.EvaluateEvidenceGate(result,
            new TimeOnly(13, 10), 2, .70m).Permitted);
    }

    private static SensexAdaptiveSetupAssessment PreferredTrend() => new("test", false,
        SensexMarketState.DirectionalTrend, SensexSetupVerdict.Prefer,
        "ContinuationOrRetest", .75m, .20m, .45m, .60m, .10m,
        .60m, .40m, 1.20m, ["Clean trend."], [], []);
}
