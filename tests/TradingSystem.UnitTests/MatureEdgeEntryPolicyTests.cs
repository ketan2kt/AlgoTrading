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

        Assert.False(MatureEdgeEntryPolicy.Evaluate(move, frames, Direction.Buy,
            Bars([100m, 102m, 100m, 101m, 101.1m])).Permitted);
    }

    [Fact]
    public void StrongResumptionAndOrdinaryPullbacksRemainEligible()
    {
        var mature = new SessionMoveQualityDecision(true, 10.26m, 10m, .51m, []);
        var ordinary = new SessionMoveQualityDecision(true, 3m, 2m, .51m, []);

        Assert.True(MatureEdgeEntryPolicy.Evaluate(mature, Frames(.55m), Direction.Buy,
            Bars([100m, 101m, 100.8m, 102m, 103m])).Permitted);
        Assert.True(MatureEdgeEntryPolicy.Evaluate(ordinary, Frames(.29m), Direction.Buy, []).Permitted);
    }

    [Fact]
    public void EarlierChopCannotVetoFreshResumptionAndEarlierTrendCannotMaskCurrentWeakness()
    {
        var move = new SessionMoveQualityDecision(true, 20m, 20m, .2m, []);
        Assert.True(MatureEdgeEntryPolicy.Evaluate(move, Frames(.1m), Direction.Buy,
            Bars([100m, 101m, 100.8m, 102m, 103m])).Permitted);
        Assert.False(MatureEdgeEntryPolicy.Evaluate(move, Frames(.9m), Direction.Buy,
            Bars([100m, 102m, 100m, 101m, 101.1m])).Permitted);
    }

    [Fact]
    public void GapsAndOpposingHigherTimeframeStillBlockEntry()
    {
        var move = new SessionMoveQualityDecision(true, 20m, 20m, .2m, []);
        var bars = Bars([100m, 101m, 100.8m, 102m, 103m]);
        Assert.False(MatureEdgeEntryPolicy.Evaluate(move,
            Frames(.9m) with { CandidateAligned = false }, Direction.Buy, bars).Permitted);
        bars[^1] = bars[^1] with { OpenTimeUtc = bars[^1].OpenTimeUtc.AddMinutes(1) };
        Assert.False(MatureEdgeEntryPolicy.Evaluate(move, Frames(.9m), Direction.Buy, bars).Permitted);
    }

    [Fact]
    public void BearishResumptionUsesTheSameRule()
    {
        var move = new SessionMoveQualityDecision(true, 20m, 20m, .2m, []);
        var frames = Frames(.1m) with { Consensus = Direction.Sell };
        Assert.True(MatureEdgeEntryPolicy.Evaluate(move, frames, Direction.Sell,
            Bars([103m, 102m, 102.2m, 101m, 100m])).Permitted);
    }

    private static StrategyPriceBar[] Bars(decimal[] closes) => closes.Select((close, index) =>
    {
        var open = index == 0 ? close : closes[index - 1];
        return new StrategyPriceBar(new DateTimeOffset(2026, 11, 6, 5, 0, 0, TimeSpan.Zero).AddMinutes(index),
            open, Math.Max(open, close) + .1m, Math.Min(open, close) - .1m, close);
    }).ToArray();

    private static MultiTimeframeDecisionAssessment Frames(decimal oneMinuteStrength) =>
        new(Direction.Buy, true, "Upper opening-range edge", 0m,
            [new("1m", Direction.Buy, oneMinuteStrength, "Bullish"),
             new("5m", Direction.Buy, .61m, "Bullish"),
             new("15m", Direction.Buy, 1m, "Bullish")], []);
}
