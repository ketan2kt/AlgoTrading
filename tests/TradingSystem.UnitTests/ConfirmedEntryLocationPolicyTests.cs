using TradingSystem.Application.Execution;
using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class ConfirmedEntryLocationPolicyTests
{
    private static readonly MarketLocationDecision Middle = new(false, "BetweenLevels", 95m, 110m,
        ["Price is in the middle of the current session range; location offers no clear edge."]);
    private static readonly MarketStructureQualitySnapshot Structure = new(
        MarketStructureQualityState.DevelopingTrend, Direction.Buy, .55m, .30m, .55m,
        1.5m, 2m, .6m, true, []);
    private static readonly MultiTimeframeDecisionAssessment Frames = new(Direction.Buy, true,
        "Opening-range middle", 2m, [], []);
    private static StrategyPriceBar[] Bars => [
        new(DateTimeOffset.UnixEpoch, 99m, 101m, 98m, 100m),
        new(DateTimeOffset.UnixEpoch.AddMinutes(1), 100m, 103m, 99m, 102m)];

    [Fact]
    public void ConfirmedDevelopingContinuationCanPassOnlyTheCoarseMiddleVeto()
    {
        Assert.True(ConfirmedEntryLocationPolicy.Evaluate(Middle, Bars,
            Direction.Buy, Structure, Frames, 2m).Permitted);
    }

    [Fact]
    public void RealResistanceAndPoorStructureAreNeverBypassed()
    {
        var resistance = Middle with { Reasons = ["Buy entry is too close to major resistance without a confirmed breakout."] };
        Assert.False(ConfirmedEntryLocationPolicy.Evaluate(resistance, Bars,
            Direction.Buy, Structure, Frames, 2m).Permitted);
        Assert.False(ConfirmedEntryLocationPolicy.Evaluate(Middle, Bars,
            Direction.Buy, Structure with { State = MarketStructureQualityState.NoisyChop }, Frames, 2m).Permitted);
        Assert.False(ConfirmedEntryLocationPolicy.Evaluate(Middle, Bars,
            Direction.Buy, Structure, Frames with { CandidateAligned = false }, 2m).Permitted);
        Assert.False(ConfirmedEntryLocationPolicy.Evaluate(Middle with { NearestResistance = 102.5m }, Bars,
            Direction.Buy, Structure, Frames, 2m).Permitted);
    }
}
