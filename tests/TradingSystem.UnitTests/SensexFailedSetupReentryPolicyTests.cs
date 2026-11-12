using TradingSystem.Application.Execution;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class SensexFailedSetupReentryPolicyTests
{
    private static readonly DateTimeOffset Exit = new(2026, 11, 12, 9, 45, 17, TimeSpan.FromHours(5.5));
    private static readonly DateTimeOffset Repeat = new(2026, 11, 12, 9, 58, 2, TimeSpan.FromHours(5.5));
    private static readonly MarketStateAssessmentV2 RecordedState = new(
        IntradayMarketStateV2.BullTrend, Direction.Buy, .70m, .1114165945m, .4642857143m,
        .05m, .8m, .65m, 5.10m, []);

    [Fact]
    public void RecordedWeakBullTrendCannotRepeatAfterALosingFailedSetupEvenOnADifferentStrike() =>
        Assert.True(SensexFailedSetupReentryPolicy.Blocks(Repeat, Exit, "FailedSetupExit", -3_770.97m,
            RecordedState, Direction.Buy));

    [Fact]
    public void ATrendLabelAloneIsInsufficientButEfficientAlignedFollowThroughCanPass()
    {
        var strong = RecordedState with { DirectionalEfficiency = .30m };
        Assert.False(SensexFailedSetupReentryPolicy.Blocks(Repeat, Exit, "FailedSetupExit", -100m,
            strong, Direction.Buy));
        Assert.True(SensexFailedSetupReentryPolicy.Blocks(Repeat, Exit, "FailedSetupExit", -100m,
            strong with { State = IntradayMarketStateV2.NoisyChop }, Direction.Buy));
        Assert.True(SensexFailedSetupReentryPolicy.Blocks(Repeat, Exit, "FailedSetupExit", -100m,
            strong with { Bias = Direction.Sell }, Direction.Buy));
        Assert.True(SensexFailedSetupReentryPolicy.Blocks(Repeat, Exit, "FailedSetupExit", -100m,
            null, Direction.Buy));
    }

    [Fact]
    public void DoesNotBlockFirstEntryNonFailedExitNonLosingExitOrAfterFifteenMinutes()
    {
        Assert.False(SensexFailedSetupReentryPolicy.Blocks(Repeat, null, null, null, RecordedState, Direction.Buy));
        Assert.False(SensexFailedSetupReentryPolicy.Blocks(Repeat, Exit, "StopLossHit", -100m, RecordedState, Direction.Buy));
        Assert.False(SensexFailedSetupReentryPolicy.Blocks(Repeat, Exit, "FailedSetupExit", 50m, RecordedState, Direction.Buy));
        Assert.False(SensexFailedSetupReentryPolicy.Blocks(Exit.AddMinutes(15), Exit, "FailedSetupExit", -100m,
            RecordedState, Direction.Buy));
        Assert.False(SensexFailedSetupReentryPolicy.Blocks(Exit.AddSeconds(-1), Exit, "FailedSetupExit", -100m,
            RecordedState, Direction.Buy));
    }

    [Fact]
    public void ProtectsPutSideUnderTheSameRule()
    {
        var weakBear = RecordedState with { State = IntradayMarketStateV2.BearTrend, Bias = Direction.Sell };
        Assert.True(SensexFailedSetupReentryPolicy.Blocks(Repeat, Exit, "FailedSetupExit", -100m,
            weakBear, Direction.Sell));
    }
}
