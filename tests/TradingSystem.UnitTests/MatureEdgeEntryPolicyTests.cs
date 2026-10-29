using TradingSystem.Application.Execution;
using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class MatureEdgeEntryPolicyTests
{
    [Fact]
    public void WeakImmediateParticipationRejectsMatureNearExtremeEntry()
    {
        var move = new SessionMoveQualityDecision(true, 10.26m, 10m, .51m, []);
        var frames = Frames(.29m);

        Assert.False(MatureEdgeEntryPolicy.Evaluate(move, frames, Direction.Buy).Permitted);
    }

    [Fact]
    public void StrongResumptionAndOrdinaryPullbacksRemainEligible()
    {
        var mature = new SessionMoveQualityDecision(true, 10.26m, 10m, .51m, []);
        var ordinary = new SessionMoveQualityDecision(true, 3m, 2m, .51m, []);

        Assert.True(MatureEdgeEntryPolicy.Evaluate(mature, Frames(.55m), Direction.Buy).Permitted);
        Assert.True(MatureEdgeEntryPolicy.Evaluate(ordinary, Frames(.29m), Direction.Buy).Permitted);
    }

    private static MultiTimeframeDecisionAssessment Frames(decimal oneMinuteStrength) =>
        new(Direction.Buy, true, "Upper opening-range edge", 0m,
            [new("1m", Direction.Buy, oneMinuteStrength, "Bullish"),
             new("5m", Direction.Buy, .61m, "Bullish"),
             new("15m", Direction.Buy, 1m, "Bullish")], []);
}
