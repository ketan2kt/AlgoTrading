using TradingSystem.Application.Execution;
using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;
using System.Text.Json;

namespace TradingSystem.UnitTests;

public sealed class NiftyCausalPullbackTests
{
    [Fact]
    public void RecordedDecisionPrefixesRejectLateEntriesButRetainFirstReclaims()
    {
        using var stream=typeof(NiftyCausalPullbackTests).Assembly.GetManifestResourceStream(
            "TradingSystem.UnitTests.RegressionFixtures.nifty-causal-pullback-202611.json")!;
        using var doc=JsonDocument.Parse(stream);
        foreach(var row in doc.RootElement.EnumerateArray())
        {
            var bars=row.GetProperty("bars").EnumerateArray().Select(b=>new StrategyPriceBar(b.GetProperty("at").GetDateTimeOffset(),
                b.GetProperty("o").GetDecimal(),b.GetProperty("h").GetDecimal(),b.GetProperty("l").GetDecimal(),b.GetProperty("c").GetDecimal())).ToArray();
            Assert.All(bars,b=>Assert.True(b.OpenTimeUtc.AddMinutes(1)<=row.GetProperty("decision").GetDateTimeOffset()));
            var tail=bars.TakeLast(15).ToArray();
            var atr=tail.Skip(1).Select((b,i)=>Math.Max(b.High-b.Low,Math.Max(Math.Abs(b.High-tail[i].Close),Math.Abs(b.Low-tail[i].Close)))).Average();
            Assert.Equal(row.GetProperty("expected").GetBoolean(),SessionMoveQualityPolicy.Evaluate(bars,Direction.Buy,atr,true).Permitted);
            var mirrored=bars.Select(b=>new StrategyPriceBar(b.OpenTimeUtc.AddDays(7),60000m-b.Open,60000m-b.Low,60000m-b.High,60000m-b.Close)).ToArray();
            Assert.Equal(row.GetProperty("expected").GetBoolean(),SessionMoveQualityPolicy.Evaluate(mirrored,Direction.Sell,atr,true).Permitted);
        }
    }

    private static StrategyPriceBar[] RisingTail()
    {
        var at=new DateTimeOffset(2026,11,20,3,45,0,TimeSpan.Zero);
        var bars=Enumerable.Range(0,35).Select(i=>new StrategyPriceBar(at.AddMinutes(i),100m+i,101m+i,99m+i,100m+i)).ToArray();
        bars[^4]=bars[^4] with {Open=131m,High=134m,Low=130m,Close=132m};
        bars[^3]=bars[^3] with {Open=132m,High=134.1m,Low=131m,Close=133m};
        bars[^2]=bars[^2] with {Open=133m,High=134.2m,Low=132m,Close=134m};
        bars[^1]=bars[^1] with {Open=134m,High=135m,Low=133m,Close=134.8m};
        return bars;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OlderLowerPricesDoNotProveARetracement(bool mirrored)
    {
        var bars=RisingTail();
        if(mirrored)bars=bars.Select(b=>new StrategyPriceBar(b.OpenTimeUtc.AddDays(7),300m-b.Open,300m-b.Low,300m-b.High,300m-b.Close)).ToArray();
        var direction=mirrored?Direction.Sell:Direction.Buy;
        Assert.True(SessionMoveQualityPolicy.Evaluate(bars,direction,10m).Permitted);
        Assert.False(SessionMoveQualityPolicy.Evaluate(bars,direction,10m,relaxedNiftyEntries:true).Permitted);
    }

    [Fact]
    public void ActualCompletedPullbackCanResumeWithoutWideningStops()
    {
        var bars=RisingTail();
        bars[^3]=bars[^3] with {Low=129m,Close=129m};
        Assert.True(SessionMoveQualityPolicy.Evaluate(bars,Direction.Buy,10m,true).Permitted);
        Assert.False(SessionMoveQualityPolicy.Evaluate(bars.Where((_,i)=>i!=bars.Length-3).ToArray(),Direction.Buy,10m,true).Permitted);
    }

    [Fact]
    public void AcceptedTwoCloseBreakoutDoesNotRequireAnArtificialPullback()
    {
        var bars=RisingTail();
        bars[^2]=bars[^2] with {Open=134m,High=134.5m,Close=134.4m};
        Assert.True(SessionMoveQualityPolicy.Evaluate(bars,Direction.Buy,10m,true).Permitted);
        bars[^2]=bars[^2] with {Close=134.1m};
        Assert.False(SessionMoveQualityPolicy.Evaluate(bars,Direction.Buy,10m,true).Permitted);
    }

    [Fact]
    public void IncompleteOrCrossSessionPatternCannotConfirmMatureEntry()
    {
        var bars=RisingTail();
        bars[^3]=bars[^3] with {Low=129m,Close=129m};
        bars[^3]=bars[^3] with {OpenTimeUtc=bars[^3].OpenTimeUtc.AddSeconds(1)};
        Assert.False(SessionMoveQualityPolicy.Evaluate(bars,Direction.Buy,10m,true).Permitted);
    }

    [Fact]
    public void OrdinaryEntryOutsideMatureExtremeIsUnchanged()
    {
        Assert.True(SessionMoveQualityPolicy.Evaluate(RisingTail(),Direction.Buy,20m,true).Permitted);
    }
}
