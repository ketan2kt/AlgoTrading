using TradingSystem.Application.Execution;
using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class NiftyEntryExitBoundaryTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 18, 4, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(Direction.Buy)]
    [InlineData(Direction.Sell)]
    public void CountertrendEntryNeedsCompletedStructureButAlignedEntryIsUnaffected(Direction direction)
    {
        StrategyPriceBar[] bars = [new(Start,100,110,95,105),new(Start.AddMinutes(5),105,112,100,106),
            new(Start.AddMinutes(10),106,111,103,109)];
        if (direction == Direction.Sell) bars = bars.Select(Mirror).ToArray();
        var opposed = new MultiTimeframeDecisionAssessment(null,false,"test",1,
            [new("5m",direction==Direction.Buy?Direction.Sell:Direction.Buy,.3m,"Opposed")],[]);
        Assert.False(NiftyCountertrendEntryPolicy.Evaluate(bars,direction,opposed,300).Permitted);
        var aligned = opposed with {Timeframes=[new("5m",direction,.3m,"Aligned")]};
        Assert.True(NiftyCountertrendEntryPolicy.Evaluate(bars,direction,aligned,300).Permitted);
        var confirmed = new StrategyPriceBar(Start.AddMinutes(15),109,115,108,114);
        if(direction==Direction.Sell)confirmed=Mirror(confirmed);
        Assert.True(NiftyCountertrendEntryPolicy.Evaluate([..bars,confirmed],direction,opposed,300).Permitted);
        Assert.False(NiftyCountertrendEntryPolicy.Evaluate([bars[0],bars[1],confirmed],direction,opposed,300).Permitted);
    }

    [Theory]
    [InlineData(Direction.Buy)]
    [InlineData(Direction.Sell)]
    public void NiftyKeepsFastExitForDecisiveFailureButNotAnOrdinaryPullback(Direction direction)
    {
        // Sell example: setup high is 110, signal high 101. A small rebound
        // across the signal/EMA is not a decisive failure of the setup.
        var bars=Enumerable.Range(0,12).Select(i=>new StrategyPriceBar(Start.AddMinutes(i),100,101,99,100)).ToArray();
        bars[7]=bars[7] with{High=110};
        bars[9]=bars[9] with{Open=101,Close=100};
        bars[10]=bars[10] with{Open=100,Low=98,Close=99};
        bars[11]=bars[11] with{Open=99,High=104,Close=103};
        var entry=Start.AddMinutes(10).AddSeconds(5);
        var at=Start.AddMinutes(12).AddSeconds(2);
        FailedSetupExitDecision Check(StrategyPriceBar[] source, decimal spot) =>
            FailedSetupExitPolicy.EvaluateForMarket("nifty","ema-pullback-continuation",direction,
                100,95,10,100,entry,at,spot,at,source,60,30);
        if(direction==Direction.Buy)bars=bars.Select(Mirror).ToArray();
        Assert.False(Check(bars,direction==Direction.Sell?105:95).ShouldExit);
        var last=new StrategyPriceBar(Start.AddMinutes(11),100,113,100,112);
        bars[11]=direction==Direction.Sell?last:Mirror(last);
        var fast=Check(bars,direction==Direction.Sell?113:87);
        Assert.True(fast.ShouldExit); // prior candle began before fill: only ONE fully post-entry close
        Assert.Contains("three-candle boundary",fast.Reasons[0],StringComparison.Ordinal);
        Assert.False(Check(bars.Where(bar=>bar.OpenTimeUtc!=Start.AddMinutes(8)).ToArray(),
            direction==Direction.Sell?113:87).ShouldExit);
        Assert.False(FailedSetupExitPolicy.EvaluateForMarket("nifty","ema-pullback-continuation",direction,
            100,95,10,100,entry,at,direction==Direction.Sell?113:87,at.AddSeconds(-31),bars,60,30).ShouldExit);
    }

    private static StrategyPriceBar Mirror(StrategyPriceBar bar) =>
        bar with{Open=200-bar.Open,High=200-bar.Low,Low=200-bar.High,Close=200-bar.Close};
}
