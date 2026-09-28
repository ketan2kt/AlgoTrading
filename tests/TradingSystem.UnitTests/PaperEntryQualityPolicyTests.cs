using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class PaperEntryQualityPolicyTests
{
    [Fact]
    public void AllowsBalancedDirectionalContinuationDespiteSoftRoomVeto()
    {
        var structure = new MarketStructureQualitySnapshot(
            MarketStructureQualityState.DevelopingTrend, Direction.Sell, .40m, .40m,
            .45m, 1.2m, .65m, .50m, false,
            ["Known room before structure is only 0.65R."]);

        var decision = PaperEntryQualityPolicy.Evaluate(structure, Direction.Sell,
            "ema-pullback-continuation", true, .65m, .77m);

        Assert.True(decision.Permitted);
    }

    [Fact]
    public void DoesNotOverrideNearZeroRoom()
    {
        var structure = new MarketStructureQualitySnapshot(
            MarketStructureQualityState.VolatilityTransition, Direction.Sell, .30m, .45m,
            .45m, 1.2m, .02m, .40m, false,
            ["Known room before structure is only 0.02R."]);

        var decision = PaperEntryQualityPolicy.Evaluate(structure, Direction.Sell,
            "ema-pullback-continuation", true, .75m, 1.5m);

        Assert.False(decision.Permitted);
    }

    [Theory]
    [InlineData(MarketStructureQualityState.NoisyChop)]
    [InlineData(MarketStructureQualityState.MatureTrend)]
    [InlineData(MarketStructureQualityState.StructuredRange)]
    [InlineData(MarketStructureQualityState.VolatilityTransition)]
    public void RejectsAmbiguousOrExhaustedStructure(MarketStructureQualityState state)
    {
        var decision = PaperEntryQualityPolicy.Evaluate(Snapshot(state, false), Direction.Buy);

        Assert.False(decision.Permitted);
        Assert.NotEmpty(decision.Reasons);
    }

    [Fact]
    public void RejectsCandidateAgainstObservedShortTermDirection()
    {
        var decision = PaperEntryQualityPolicy.Evaluate(
            Snapshot(MarketStructureQualityState.DevelopingTrend, true) with
            { ObservedBias = Direction.Sell }, Direction.Buy);

        Assert.False(decision.Permitted);
        Assert.Contains(decision.Reasons, reason => reason.Contains("conflicts"));
    }

    [Fact]
    public void PermitsAlignedDevelopingTrend()
    {
        var decision = PaperEntryQualityPolicy.Evaluate(
            Snapshot(MarketStructureQualityState.DevelopingTrend, true), Direction.Buy);

        Assert.True(decision.Permitted);
        Assert.Empty(decision.Reasons);
    }

    [Theory]
    [InlineData("exploration-opening-range-breakout")]
    [InlineData("exploration-momentum-expansion")]
    public void PermitsVolatilityTransitionForBreakoutExploration(string strategyId)
    {
        var decision = PaperEntryQualityPolicy.Evaluate(
            Snapshot(MarketStructureQualityState.VolatilityTransition, true),
            Direction.Buy, strategyId);

        Assert.True(decision.Permitted);
    }

    private static MarketStructureQualitySnapshot Snapshot(
        MarketStructureQualityState state, bool permit) => new(
        state, Direction.Buy, 0.5m, 0.3m, 0.6m, 1.5m, 2m, 0.6m, permit,
        permit ? ["No shadow structure-quality veto was triggered."] :
        ["Frequent direction changes and low path efficiency indicate noisy chop."]);
}
