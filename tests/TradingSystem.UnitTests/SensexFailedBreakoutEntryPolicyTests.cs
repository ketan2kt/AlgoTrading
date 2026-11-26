using TradingSystem.Application.Execution;
using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class SensexFailedBreakoutEntryPolicyTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 26, 3, 45, 0, TimeSpan.Zero);
    private static MarketStateAssessmentV2 Range(Direction side) => new(
        IntradayMarketStateV2.StructuredRange, side, .7m, .1m, .5m, .1m,
        side == Direction.Buy ? .9m : .1m, .8m, 7m, []);

    private static StrategyPriceBar[] FailedBreak(Direction side)
    {
        var bars = Enumerable.Range(0, 15).Select(i =>
            new StrategyPriceBar(Start.AddMinutes(i), 100m, 100.5m, 99.5m, 100m)).ToList();
        bars.Add(new(Start.AddMinutes(15), 100.5m, 103m, 100.5m, 102.5m));
        bars.Add(new(Start.AddMinutes(16), 102.5m, 102.8m, 99m, 99.5m));
        bars.Add(new(Start.AddMinutes(17), 99.5m, 102m, 99m, 101.8m));
        return side == Direction.Buy ? bars.ToArray() : bars.Select(b => b with
        { Open = 200m-b.Open, High = 200m-b.Low, Low = 200m-b.High, Close = 200m-b.Close }).ToArray();
    }

    [Theory]
    [InlineData(Direction.Buy)]
    [InlineData(Direction.Sell)]
    public void BounceAfterFullyErasedRangeBreakRequiresConfirmation(Direction side) =>
        Assert.NotNull(SensexFailedBreakoutEntryPolicy.Rejection(FailedBreak(side), side, Range(side)));

    [Theory]
    [InlineData(Direction.Buy, Direction.Sell)]
    [InlineData(Direction.Sell, Direction.Buy)]
    public void FailedContinuationDoesNotAutomaticallyAuthorizeTheOppositeRangeFade(Direction failed, Direction fade)
    {
        var bars = FailedBreak(failed);
        Assert.NotNull(SensexFailedBreakoutEntryPolicy.OppositeRangeRejection(bars, fade, Range(failed)));
        bars[^1] = bars[^1] with { Close = fade == Direction.Buy ? 102m : 98m,
            High = fade == Direction.Buy ? 102m : bars[^1].High,
            Low = fade == Direction.Sell ? 98m : bars[^1].Low };
        Assert.Null(SensexFailedBreakoutEntryPolicy.OppositeRangeRejection(bars, fade, Range(failed)));
    }

    [Theory]
    [InlineData(Direction.Buy)]
    [InlineData(Direction.Sell)]
    public void ReclaimedFailedExtremeReopensEntry(Direction side)
    {
        var bars = FailedBreak(side);
        bars[^1] = bars[^1] with { Close = side == Direction.Buy ? 104m : 96m,
            High = side == Direction.Buy ? 104m : bars[^1].High,
            Low = side == Direction.Sell ? 96m : bars[^1].Low };
        Assert.Null(SensexFailedBreakoutEntryPolicy.Rejection(bars, side, Range(side)));
    }

    [Fact]
    public void RebuiltAcceptedPivotCanResumeBelowOldHigh()
    {
        var bars = FailedBreak(Direction.Buy).Take(17).ToList();
        for (var i=0; i<3; i++) bars.Add(new(Start.AddMinutes(17+i),99.5m,100m,99m,99.8m));
        bars.Add(new(Start.AddMinutes(20),99.8m,100.9m,99.8m,100.8m));
        bars.Add(new(Start.AddMinutes(21),100.8m,101.2m,100.7m,101.1m));
        Assert.Null(SensexFailedBreakoutEntryPolicy.Rejection(bars,Direction.Buy,Range(Direction.Buy)));
        Assert.Null(SensexFailedBreakoutEntryPolicy.OppositeRangeRejection(bars,Direction.Sell,Range(Direction.Buy)));
    }

    [Fact]
    public void MissingCandleCannotCreateAFailedBreakoutForEitherPath()
    {
        var bars = FailedBreak(Direction.Buy);
        bars[^1] = bars[^1] with { OpenTimeUtc = bars[^1].OpenTimeUtc.AddMinutes(1) };
        Assert.Null(SensexFailedBreakoutEntryPolicy.Rejection(bars, Direction.Buy, Range(Direction.Buy)));
        Assert.Null(SensexFailedBreakoutEntryPolicy.OppositeRangeRejection(bars, Direction.Sell, Range(Direction.Buy)));
    }

    [Fact]
    public void DirectionalMoveAndRangeMiddleRemainEligible()
    {
        var bars=FailedBreak(Direction.Buy);
        Assert.Null(SensexFailedBreakoutEntryPolicy.Rejection(bars,Direction.Buy,
            Range(Direction.Buy) with { DirectionalEfficiency=.6m }));
        Assert.Null(SensexFailedBreakoutEntryPolicy.Rejection(bars,Direction.Buy,
            Range(Direction.Buy) with { RangePosition=.5m }));
    }

    [Fact]
    public void OrdinaryPullbackThatDoesNotEraseBreakoutIsUnchanged()
    {
        var bars=FailedBreak(Direction.Buy);
        bars[^2]=bars[^2] with { Close=101m };
        Assert.Null(SensexFailedBreakoutEntryPolicy.Rejection(bars,Direction.Buy,Range(Direction.Buy)));
    }

    [Fact]
    public void GapOnlyDojiIsNotAFailedDirectionalImpulse()
    {
        var bars=FailedBreak(Direction.Buy);
        bars[^3]=bars[^3] with { Open=102.5m,High=102.5m,Low=102.5m,Close=102.5m };
        Assert.Null(SensexFailedBreakoutEntryPolicy.Rejection(bars,Direction.Buy,Range(Direction.Buy)));
    }

    [Fact]
    public void TinyBodyBreakIsNotMisclassifiedAsAFailedImpulse()
    {
        var bars = FailedBreak(Direction.Buy);
        bars[^3] = bars[^3] with { Open = 102.45m };
        Assert.Null(SensexFailedBreakoutEntryPolicy.Rejection(bars, Direction.Buy, Range(Direction.Buy)));
        Assert.Null(SensexFailedBreakoutEntryPolicy.OppositeRangeRejection(bars, Direction.Sell, Range(Direction.Buy)));
    }

    [Fact]
    public void SharedSelectionRejectsEarlyCandidateWithoutFallingThroughToAnotherPath()
    {
        var bars=FailedBreak(Direction.Buy);
        var lifecycle=new SensexSetupLifecycle(SensexSetupPhase.EntryWindow,Direction.Buy,3,.1m,.5m,[]);
        var selected=SensexEntrySelectionPolicy.Evaluate(new(null,.5m,"Index momentum breakout",[]),
            bars,lifecycle,null,Range(Direction.Buy),100m,101m,100m,false);
        Assert.Null(selected.Candidate.Direction);
        Assert.True(selected.Supplemental);
        Assert.Contains(selected.Candidate.Reasons,r=>r.Contains("range breakout"));
    }
}
