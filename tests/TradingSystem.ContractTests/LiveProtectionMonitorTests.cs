using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Diagnostics;

namespace TradingSystem.ContractTests;

public class LiveProtectionMonitorTests
{
    private static readonly DateTimeOffset Now=new(2026,11,5,6,0,0,TimeSpan.Zero);
    private static LiveExecutionIntent Open(){
        var i=new LiveExecutionIntent(Guid.NewGuid(),"MARKET_POSITION",Guid.NewGuid(),"BANK-NIFTY",Guid.NewGuid(),
            Direction.Buy,150,100,90,115,"test",Now.AddMinutes(-2));
        i.Filled(150,100);i.Protected("stop",Now.AddMinutes(-1));return i;
    }
    [Theory]
    [InlineData("stop",150,90,"Last confirmed match")]
    [InlineData("other",150,90,"No confirmation for current stop order")]
    [InlineData("stop",75,90,"Quantity mismatch")]
    [InlineData("stop",150,89,"Stop price mismatch")]
    public void RequiresSameOrderPriceAndQuantity(string order,int qty,int price,string finding){
        var i=Open();
        var row=EfLiveProtectionMonitor.Assess(i,"BANKNIFTY",90,150,null,
            [new(i.SourceId,Now.AddSeconds(-3),StopComparison.Kind.Ack,price,qty,order)],Now);
        Assert.Equal(finding,row.Finding);Assert.Equal(finding!="Last confirmed match",row.NeedsAttention);
    }
    [Fact]
    public void ConfirmedStopForFullSizeDoesNotCoverPartialOrSurviveUncertainty(){
        var i=Open();i.RecordPartialExit(75,Now);
        var ack=new StopComparison.Point(i.SourceId,Now.AddSeconds(-5),StopComparison.Kind.Ack,90,150,"stop");
        Assert.Equal("Quantity mismatch",EfLiveProtectionMonitor.Assess(i,"X",90,75,null,[ack],Now).Finding);
        Assert.Equal("Stop confirmation uncertain",EfLiveProtectionMonitor.Assess(i,"X",90,75,null,
            [ack,new(i.SourceId,Now,StopComparison.Kind.Uncertain,null,null)],Now).Finding);
        Assert.True(EfLiveProtectionMonitor.Assess(i,"X",90,75,Now,[ack with {Qty=75}],Now).NeedsAttention);
    }
}
