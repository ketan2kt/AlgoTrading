using System.Text.Json;
using TradingSystem.Application.Execution;

namespace TradingSystem.UnitTests;

public sealed class HeroZeroBasketProtectionPolicyTests
{
    private static HeroZeroBasketProtectionState Evaluate(decimal? net, bool book = true,
        HeroZeroBasketProtectionState? previous = null, bool forced = false, decimal debit = 10000m) =>
        HeroZeroBasketProtectionPolicy.Evaluate(debit, net, book, previous, 35m, 15m, .25m, forced);

    [Fact]
    public void LossLimitScalesWithTheWholePairDebitAndIncludesCostsInItsNetInput()
    {
        Assert.Null(Evaluate(-3499m).ExitReason);
        Assert.Equal("HeroZeroBasketStop", Evaluate(-3500m).ExitReason);
        Assert.Equal("HeroZeroBasketStop", Evaluate(-1750m, debit: 5000m).ExitReason);
        Assert.Equal("HeroZeroBasketStop", Evaluate(-3500m, book: false).ExitReason);
    }

    [Fact]
    public void ActivationUsesNetBasketProfitNotWinningLegGrossProfit()
    {
        Assert.False(Evaluate(1499m).ProfitActivated);
        var activated = Evaluate(1500m);
        Assert.True(activated.ProfitActivated);
        Assert.Equal(1125m, activated.ProfitFloor);
        Assert.Null(Evaluate(1126m, previous: activated).ExitReason);
        Assert.Equal("HeroZeroBasketProfitProtection", Evaluate(1125m, previous: activated).ExitReason);
    }

    [Fact]
    public void TrailingFloorOnlyRisesAndSurvivesASerializedRestart()
    {
        var first = Evaluate(2000m);
        var restored = JsonSerializer.Deserialize<HeroZeroBasketProtectionState>(JsonSerializer.Serialize(first));
        var next = Evaluate(4000m, previous: restored);
        Assert.Equal(3000m, next.ProfitFloor);
        Assert.Equal(4000m, next.PeakNetPnl);
        var pullback = Evaluate(3500m, previous: next);
        Assert.Equal(3000m, pullback.ProfitFloor);
        Assert.Null(pullback.ExitReason);
        var exit = Evaluate(3000m, previous: pullback);
        var latched = JsonSerializer.Deserialize<HeroZeroBasketProtectionState>(JsonSerializer.Serialize(exit));
        Assert.Equal(exit, Evaluate(10000m, previous: latched));
    }

    [Fact]
    public void MissingBookCannotCreateAProfitWatermarkAndMissingMarksCannotInventAnExitFill()
    {
        var unknownBook = Evaluate(5000m, book: false);
        Assert.False(unknownBook.ProfitActivated);
        Assert.Equal(0m, unknownBook.PeakNetPnl);
        var protectedState = Evaluate(2000m);
        Assert.Equal(protectedState, Evaluate(null, previous: protectedState));
        Assert.Equal("HeroZeroTimeExit", Evaluate(null, previous: protectedState, forced: true).ExitReason);
        Assert.Equal("HeroZeroBasketProfitProtection", Evaluate(1400m, book: false, previous: protectedState).ExitReason);
    }

    [Fact]
    public void InconsistentStoredBasketStateRequiresLiquidationInsteadOfResettingTheFloor()
    {
        var previous = Evaluate(2000m);
        Assert.Equal("HeroZeroProtectionStateInvalid", Evaluate(2000m, previous: previous, debit: 5000m).ExitReason);
    }

    [Fact]
    public void ExistingLegDisasterStopLiquidatesTheBasketRatherThanLeavingAnAccidentalRunner()
    {
        var state = HeroZeroBasketProtectionPolicy.Evaluate(10000, null, false, null, 35, 15, .25m,
            false, legRiskBreached: true);
        Assert.Equal("HeroZeroLegRiskExit", state.ExitReason);
    }
}
