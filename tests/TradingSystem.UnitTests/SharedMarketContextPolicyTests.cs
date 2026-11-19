using TradingSystem.Application.Execution;
using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class SharedMarketContextPolicyTests
{
    private static readonly DateTimeOffset Start = new(2026,11,17,3,45,0,TimeSpan.Zero);
    private static StrategyPriceBar[] Trend(int count = 90) => Enumerable.Range(0,count)
        .Select(i=>new StrategyPriceBar(Start.AddMinutes(i),100m+i,101.2m+i,99.8m+i,101m+i)).ToArray();
    private static StrategyPriceBar[] Chop(int count = 60) => Enumerable.Range(0,count)
        .Select(i=>new StrategyPriceBar(Start.AddMinutes(i),i%2==0?102m:100m,104m,98m,i%2==0?100m:102m)).ToArray();

    [Fact]
    public void TrendAllowsAlignedContinuationWithoutAnAdxOrTimeGate()
    {
        var bars=Trend(35);
        var context=SharedMarketContextPolicy.Analyze(bars);
        Assert.Equal(SharedMarketRegime.Directional,context.Regime);
        Assert.Equal(Direction.Buy,context.Direction);
        Assert.True(SharedMarketContextPolicy.Select(context,bars,Direction.Buy,EntryStrategyFamily.Continuation).Permitted);
        Assert.False(SharedMarketContextPolicy.Select(context,bars,Direction.Sell,EntryStrategyFamily.Breakout).Permitted);
    }

    [Fact]
    public void OrdinaryPullbackRetainsEstablishedDirection()
    {
        var trend=Trend(60);
        var pullback=Enumerable.Range(0,5).Select(i=>new StrategyPriceBar(Start.AddMinutes(60+i),160m-i*.3m,160.2m-i*.3m,159.4m-i*.3m,159.7m-i*.3m));
        var bars=trend.Concat(pullback).ToArray();
        var context=SharedMarketContextPolicy.Analyze(bars);
        Assert.Equal(Direction.Buy,context.Direction);
        Assert.Equal(SharedMarketRegime.Directional,context.Regime);
    }

    [Fact]
    public void TwoCompletedStructuralBreaksInvalidateOldDirection()
    {
        var bars=Trend(60).Concat(Enumerable.Range(0,10).Select(i=>
            new StrategyPriceBar(Start.AddMinutes(60+i),100m-i,100.2m-i,98.8m-i,99m-i))).ToArray();
        Assert.NotEqual(Direction.Buy,SharedMarketContextPolicy.Analyze(bars).Direction);
    }

    [Fact]
    public void ProvisionalFiveMinuteBucketCannotMoveProtectedBoundary()
    {
        var bars=Trend(60);
        var complete=SharedMarketContextPolicy.Analyze(bars);
        var partial=bars.Concat(Enumerable.Range(0,4).Select(i=>
            new StrategyPriceBar(Start.AddMinutes(60+i),90m,91m,88m,89m))).ToArray();
        var observed=SharedMarketContextPolicy.Analyze(partial);
        Assert.Equal(complete.Direction,observed.Direction);
        Assert.Equal(complete.StructuralBoundary,observed.StructuralBoundary);
        Assert.Equal(complete.StructureAsOf,observed.StructureAsOf);
    }

    [Fact]
    public void GapResetsMemoryAndDoesNotBecomeChop()
    {
        var bars=Trend(60).Concat([new StrategyPriceBar(Start.AddMinutes(62),100m,101m,99m,100m)]).ToArray();
        var context=SharedMarketContextPolicy.Analyze(bars);
        Assert.Equal(SharedMarketRegime.WarmingUp,context.Regime);
        Assert.Null(context.Direction);
    }

    [Fact]
    public void PriorSessionCannotSupplyTodayDirection()
    {
        var prior=Trend();
        var next=new StrategyPriceBar(Start.AddDays(1),100m,101m,99m,100m);
        Assert.Equal(SharedMarketRegime.WarmingUp,SharedMarketContextPolicy.Analyze(prior.Append(next).ToArray()).Regime);
    }

    [Fact]
    public void StaleContextCannotBeReusedForALaterDecision()
    {
        var bars=Trend();
        Assert.False(SharedMarketContextPolicy.Select(SharedMarketContextPolicy.Analyze(bars[..^1]),bars,
            Direction.Buy,EntryStrategyFamily.Continuation).Permitted);
    }

    [Fact]
    public void RotatingMiddleRejectsImpulseButConfirmedEscapeRemainsEligible()
    {
        var bars=Chop();
        var context=SharedMarketContextPolicy.Analyze(bars);
        Assert.Equal(SharedMarketRegime.NoisyChop,context.Regime);
        Assert.False(SharedMarketContextPolicy.Select(context,bars,Direction.Buy,EntryStrategyFamily.Breakout).Permitted);
        var escape=bars.Concat([new(Start.AddMinutes(60),103m,110m,102m,109m),
            new StrategyPriceBar(Start.AddMinutes(61),109m,112m,108m,111m)]).ToArray();
        Assert.True(SharedMarketContextPolicy.Select(SharedMarketContextPolicy.Analyze(escape),escape,
            Direction.Buy,EntryStrategyFamily.Breakout).Permitted);
        Assert.False(SharedMarketContextPolicy.Select(SharedMarketContextPolicy.Analyze(escape[..^1]),escape[..^1],
            Direction.Buy,EntryStrategyFamily.Breakout).Permitted);
    }

    [Fact]
    public void ExistingRangeEdgeRejectionIsEligibleWithoutInventingANewStop()
    {
        var bars=Chop().Append(new StrategyPriceBar(Start.AddMinutes(60),98.2m,99m,97.9m,98.7m)).ToArray();
        var admission=SharedMarketContextPolicy.Select(SharedMarketContextPolicy.Analyze(bars),bars,
            Direction.Buy,EntryStrategyFamily.RangeRejection);
        Assert.True(admission.Permitted);
        Assert.Equal(2,admission.Priority);
    }

    [Fact]
    public void AcceptedContinuationIsNotVetoedBySlowContextAndOutranksRangeImpulse()
    {
        var trend=Trend();
        Assert.True(SharedMarketContextPolicy.Select(SharedMarketContextPolicy.Analyze(trend),trend,
            Direction.Sell,EntryStrategyFamily.Continuation).Permitted);
        var chop=Chop();
        Assert.Equal(2,SharedMarketContextPolicy.Select(SharedMarketContextPolicy.Analyze(chop),chop,
            Direction.Buy,EntryStrategyFamily.Continuation).Priority);
    }

    [Fact]
    public void InvalidatedOldThesisDoesNotReappearFromLaggingWindowOnNextBucket()
    {
        var bars=Trend(60).Concat(Enumerable.Range(0,15).Select(i=>
            new StrategyPriceBar(Start.AddMinutes(60+i),100m-i,100.2m-i,98.8m-i,99m-i))).ToArray();
        Assert.NotEqual(Direction.Buy,SharedMarketContextPolicy.Analyze(bars).Direction);
    }

    [Fact]
    public void VolatilityDoesNotReplaceDirection()
    {
        var bars=Trend().Concat(Enumerable.Range(0,5).Select(i=>
            new StrategyPriceBar(Start.AddMinutes(90+i),190m+i*4m,195m+i*4m,189m+i*4m,194m+i*4m))).ToArray();
        var context=SharedMarketContextPolicy.Analyze(bars);
        Assert.Equal(Direction.Buy,context.Direction);
        Assert.Equal(MarketVolatilityState.Expanded,context.Volatility);
    }

    [Fact]
    public void RestartAndDifferentIndexEvaluationCannotLeakState()
    {
        var bars=Trend();
        var first=SharedMarketContextPolicy.Analyze(bars);
        _=SharedMarketContextPolicy.Analyze(Chop());
        var replay=SharedMarketContextPolicy.Analyze(bars);
        Assert.Equal(first.Direction,replay.Direction);
        Assert.Equal(first.Regime,replay.Regime);
        Assert.Equal(first.StructuralBoundary,replay.StructuralBoundary);
        Assert.Equal(first.AsOf,replay.AsOf);
    }

    [Theory]
    [InlineData("ema-pullback-continuation",EntryStrategyFamily.Continuation)]
    [InlineData("Sensex persistent directional thesis",EntryStrategyFamily.Continuation)]
    [InlineData("Sensex range-edge rejection",EntryStrategyFamily.RangeRejection)]
    [InlineData("Bank index directional breakout",EntryStrategyFamily.Breakout)]
    public void ClassifiesExistingStrategyNames(string name,EntryStrategyFamily family) =>
        Assert.Equal(family,SharedMarketContextPolicy.Family(name));
}
