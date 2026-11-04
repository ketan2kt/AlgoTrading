using TradingSystem.Application.Execution;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class NiftySignalRecoveryPolicyTests
{
    [Fact]
    public void RecoveryPreservesCurrentProductionFingerprintAndTransitionPlan()
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
