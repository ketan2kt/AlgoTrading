using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class PaperEntryQualityPolicyTests
{
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

    private static MarketStructureQualitySnapshot Snapshot(
        MarketStructureQualityState state, bool permit) => new(
        state, Direction.Buy, 0.5m, 0.3m, 0.6m, 1.5m, 2m, 0.6m, permit,
        permit ? ["No shadow structure-quality veto was triggered."] :
        ["Frequent direction changes and low path efficiency indicate noisy chop."]);
}
