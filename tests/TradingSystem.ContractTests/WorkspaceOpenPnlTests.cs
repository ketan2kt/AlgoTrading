using TradingSystem.Application.Execution;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.MarketData;

namespace TradingSystem.ContractTests;

public sealed class WorkspaceOpenPnlTests
{
    [Theory]
    [InlineData("nifty", "NSE")]
    [InlineData("bank-nifty", "NSE")]
    [InlineData("sensex", "BSE")]
    public void OpenPositionIncludesBookedPartialsAndCostsWithoutMutatingLedger(string market, string exchange)
    {
        var at=DateTimeOffset.UtcNow;
        var p=new MarketPaperPosition(Guid.NewGuid(),market,Guid.NewGuid(),Guid.NewGuid(),"test",Direction.Buy,100,100,90,120,at);
        p.TakePartial(120,40,100,at);p.Mark(110,90,at);
        var before=(p.RealisedPnl,p.CostsPaid,p.RemainingQuantity,p.Status);
        var costs=PaperTradingCostModel.CalculateOptionCharges([new(PaperOptionTransactionSide.Buy,100,60),new(PaperOptionTransactionSide.Sell,110,60)],exchange).Total;
        var estimate=WorkspaceOpenPnl.FromPosition(p,exchange)!;
        Assert.Equal(1400m,estimate.Gross);Assert.Equal(100+costs,estimate.Costs);Assert.Equal(1300-costs,estimate.Net);
        Assert.Equal(before,(p.RealisedPnl,p.CostsPaid,p.RemainingQuantity,p.Status));
        p.Close(110,costs,"StopLossHit",at);
        Assert.Equal(estimate.Net,p.RealisedPnl);Assert.Null(WorkspaceOpenPnl.FromPosition(p,exchange));
    }

    [Theory]
    [InlineData(Direction.Buy, 1)]
    [InlineData(Direction.Sell, -1)]
    public void NiftyJournalsIncludeAddOnAndPartialOnce(Direction direction, int multiplier)
    {
        var id=Guid.NewGuid();
        var orders=new Dictionary<string,string>{[$"{id:N}-ENTRY"]=Fill(100,100),[$"{id:N}-ADDON"]=Fill(50,110),[$"{id:N}-PARTIAL"]=Fill(60,120)};
        var estimate=WorkspaceOpenPnl.FromJournal(id,direction,115,orders)!;
        var side=direction==Direction.Buy ? PaperOptionTransactionSide.Buy : PaperOptionTransactionSide.Sell;
        var exit=direction==Direction.Buy ? PaperOptionTransactionSide.Sell : PaperOptionTransactionSide.Buy;
        var charges=PaperTradingCostModel.CalculateOptionCharges([new(side,100,100),new(side,110,50),new(exit,120,60),new(exit,115,90)]).Total;
        Assert.Equal(2050m*multiplier,estimate.Gross);Assert.Equal(charges,estimate.Costs);Assert.Equal(estimate.Gross-charges,estimate.Net);
        Assert.Equal(3,orders.Count);
    }

    [Fact]
    public void NiftyBeforePartialSubtractsRoundTripCostsFromGrossMark()
    {
        var id=Guid.NewGuid();
        var orders=new Dictionary<string,string>{[$"{id:N}-ENTRY"]=Fill(650,100)};
        var estimate=WorkspaceOpenPnl.FromJournal(id,Direction.Buy,110,orders)!;
        var charges=PaperTradingCostModel.CalculateOptionCharges([new(PaperOptionTransactionSide.Buy,100,650),new(PaperOptionTransactionSide.Sell,110,650)]).Total;
        Assert.Equal(6500m,estimate.Gross);Assert.Equal(6500-charges,estimate.Net);
    }

    [Fact]
    public void MissingMarkAndMalformedReceiptsAreNotReportedAsZeroProfit()
    {
        var id=Guid.NewGuid();var orders=new Dictionary<string,string>{[$"{id:N}-ENTRY"]=Fill(100,100)};
        Assert.Null(WorkspaceOpenPnl.FromJournal(id,Direction.Buy,null,orders));
        Assert.Null(WorkspaceOpenPnl.FromJournal(id,Direction.Buy,110,new Dictionary<string,string>()));
        orders[$"{id:N}-PARTIAL"]="bad json";Assert.Null(WorkspaceOpenPnl.FromJournal(id,Direction.Buy,110,orders));
        orders[$"{id:N}-PARTIAL"]=Fill(110,120);Assert.Null(WorkspaceOpenPnl.FromJournal(id,Direction.Buy,110,orders));
    }
    private static string Fill(int quantity,decimal price)=>System.Text.Json.JsonSerializer.Serialize(new{cumulativeFilledQuantity=quantity,averageFillPrice=price});
}
