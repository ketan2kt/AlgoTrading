using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Diagnostics;
using static TradingSystem.Infrastructure.Diagnostics.LiveFillPnl;

namespace TradingSystem.ContractTests;

public sealed class LiveFillPnlTests
{
    [Fact]
    public void PartialExitAndAddOnCompareOnlyLiveUnitsAndCountRecoveredOrderOnce()
    {
        var at=DateTimeOffset.UnixEpoch;
        var intent=MakeIntent();
        intent.Submitted("entry",at);
        intent.Filled(50,102);
        intent.Protected("stop",at);
        intent.RecordAddOn(10,at);
        intent.RecordPartialExit(20,at);
        intent.Closed(at);
        var id=intent.SourceId;
        Observation[] fills=[new(id,Leg.Entry,"entry",50,102,50),new(id,Leg.AddOn,"add",10,110,10),
            new(id,Leg.Partial,"partial",20,115,20),new(id,Leg.Final,"stop",40,112,40)];
        var paper=new Dictionary<Leg,PaperLeg> { [Leg.Entry]=new(100,100),[Leg.AddOn]=new(10,110),
            [Leg.Partial]=new(20,116),[Leg.Final]=new(90,113) };
        var r=Calculate(intent,fills.Append(fills[3]),paper);
        Assert.Equal("Closed",r.Status);
        Assert.Equal(580m,r.LiveGross);
        Assert.Equal(740m,r.MatchedPaperGross);
        Assert.Equal(-160m,r.ExecutionDifference);
        Assert.Equal(-100m,r.EntryDifference);
        Assert.Equal(-60m,r.ExitDifference);
        Assert.True(r.EstimatedCharges>0);
        Assert.Equal(r.LiveGross-r.EstimatedCharges,r.EstimatedNet);
        paper.Remove(Leg.Partial);
        Assert.Null(Calculate(intent,fills,paper).ExecutionDifference);
        var gap=Calculate(intent,fills[..3],paper);
        Assert.Null(gap.LiveGross);
        Assert.Equal(1,Summarize([r,gap]).Incomplete);
        Assert.Equal(580m,Summarize([r,gap]).ConfirmedGrossSubtotal);
    }

    [Fact]
    public void MissingOrConflictingPricesAreNotProfitAndMissingTradesAreNotZero()
    {
        var intent=MakeIntent();
        intent.Submitted("entry",DateTimeOffset.UnixEpoch);
        intent.Closed(DateTimeOffset.UnixEpoch);
        var id=intent.SourceId;
        var buy=new Observation(id,Leg.Entry,"entry",100,140,100);
        var sell=new Observation(id,Leg.Final,"stop",100,150,100);
        var paper=new Dictionary<Leg,PaperLeg>();
        Assert.Null(Calculate(intent,[buy,sell with { Priced=90 }],paper).LiveGross);
        Assert.Null(Calculate(intent,[buy,sell,sell with { Price=151 }],paper).LiveGross);
        var skipped=Calculate(null,[],paper);
        var day=Summarize([skipped]);
        Assert.Equal(1,day.PaperOnly);
        Assert.Null(day.EstimatedNetSubtotal);
        Assert.Null(ReadPaperLeg("""{"averageFillPrice":140,"cumulativeFilledQuantity":"unknown"}"""));
    }

    private static LiveExecutionIntent MakeIntent() => new(Guid.NewGuid(),"test",Guid.NewGuid(),"NIFTY",
        Guid.NewGuid(),Direction.Buy,100,100,90,120,"reference",DateTimeOffset.UnixEpoch);
}
