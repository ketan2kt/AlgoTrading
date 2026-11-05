using TradingSystem.Application.Execution;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class NiftySignalRecoveryPolicyTests
{
    [Theory]
    [InlineData("{\"regime\":1}", MarketRegime.StrongBullishTrend, true)]
    [InlineData("{\"regime\":3}", MarketRegime.StrongBearishTrend, true)]
    [InlineData("{\"regime\":8}", MarketRegime.GapUpContinuation, true)]
    [InlineData("{\"regime\":\"GapDownContinuation\"}", MarketRegime.GapDownContinuation, true)]
    [InlineData("{\"regime\":5}", MarketRegime.RangeBound, false)]
    [InlineData("{\"regime\":2}", MarketRegime.WeakBullishTrend, false)]
    [InlineData("{}", MarketRegime.Uncertain, false)]
    [InlineData("{\"regime\":999}", MarketRegime.Uncertain, false)]
    [InlineData("{\"regime\":\"not-a-regime\"}", MarketRegime.Uncertain, false)]
    [InlineData("{\"regime\":null}", MarketRegime.Uncertain, false)]
    [InlineData("[]", MarketRegime.Uncertain, false)]
    [InlineData("broken json", MarketRegime.Uncertain, false)]
    public void EveryRecoveryRestoresSavedRegimeWithoutInventingMissingEvidence(
        string evidence, MarketRegime expected, bool runnerEligible)
    {
        var at = new DateTimeOffset(2026, 11, 5, 4, 0, 0, TimeSpan.Zero);
        var signal = new Signal(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Direction.Buy, 100m, 90m, 110m, .8m, at, at.AddMinutes(5),
            "momentum-expansion:1.0.0:signal-id", evidence);
        var restored = NiftySignalRecoveryPolicy.Recover(signal);
        Assert.Equal(expected, restored.Regime);
        Assert.Equal(restored.Regime, NiftySignalRecoveryPolicy.Recover(signal).Regime);
        Assert.Equal(runnerEligible, NiftySignalRecoveryPolicy.ExitPlan(restored.Regime).ExtendTarget);
        Assert.Equal(90m, restored.ProposedStopLoss);
        Assert.Equal(110m, restored.ProposedTarget);
        Assert.Equal(.8m, restored.Confidence);
    }

    [Fact]
    public void LegacyRecoveryPreservesFingerprintAndConservativeTransitionPlan()
    {
        var at = new DateTimeOffset(2026, 11, 4, 4, 0, 0, TimeSpan.Zero);
        var signal = new Signal(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Direction.Buy, 100m, 90m, 115m, .8m, at, at.AddMinutes(5),
            "momentum-expansion:1.0.0:signal-id", "{}");
        var restored = NiftySignalRecoveryPolicy.Recover(signal);
        Assert.Equal(signal.Id, restored.SignalId);
        Assert.Equal("momentum-expansion", restored.StrategyId);
        Assert.Equal("1.0.0", restored.StrategyVersion);
        Assert.Equal(MarketRegime.Uncertain, restored.Regime);
        var expected = ProfessionalExitPolicy.For(ProfessionalTradeRegime.Transition);
        Assert.Equal(expected, NiftySignalRecoveryPolicy.ExitPlan());
        Assert.False(expected.ExtendTarget);
    }
}
