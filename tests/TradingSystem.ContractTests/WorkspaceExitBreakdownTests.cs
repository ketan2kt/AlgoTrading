using TradingSystem.Application.MarketData;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.MarketData;

namespace TradingSystem.ContractTests;

public sealed class WorkspaceExitBreakdownTests
{
    [Fact]
    public void NiftyUsesBothRecordedExitsWithoutRoundingOrChangingPnl()
    {
        var partial = WorkspaceExitBreakdownBuilder.FromJournal("Partial",
            "{\"cumulativeFilledQuantity\":325,\"averageFillPrice\":194.15}");
        var final = WorkspaceExitBreakdownBuilder.FromJournal("Final",
            "{\"cumulativeFilledQuantity\":325,\"averageFillPrice\":191.20}");
        var result = WorkspaceExitBreakdownBuilder.Build(650, 180.40m, 7978.75m,
            Direction.Buy, [partial!, final!]);
        Assert.True(result.Complete);
        Assert.Equal(192.675m, result.AveragePrice);
        Assert.Equal(7978.75m, (result.AveragePrice - 180.40m) * 650);
        Assert.Equal(191.20m, result.Fills[1].Price);
    }

    [Theory]
    [InlineData("sensex",100,736,40,814.9,830.15,8805)]
    [InlineData("bank-nifty",150,397.25,60,428.65,412.35,3243)]
    public void MarketPositionsRetainPartialAndFinalEconomics(string market, int qty, decimal entry,
        int partialQty, decimal partialPrice, decimal finalPrice, decimal gross)
    {
        var at = new DateTimeOffset(2026,11,19,4,0,0,TimeSpan.Zero);
        var position = new MarketPaperPosition(Guid.NewGuid(),market,Guid.NewGuid(),Guid.NewGuid(),
            "test",Direction.Buy,qty,entry,entry*.9m,entry*1.1m,at);
        position.TakePartial(partialPrice,partialQty,100m,at.AddMinutes(1));
        position.Close(finalPrice,150m,"StopLossHit",at.AddMinutes(2));
        var before = (position.RealisedPnl,position.CostsPaid,position.CurrentPrice);
        var result = WorkspaceExitBreakdownBuilder.FromPosition(position);
        Assert.True(result.Complete);
        Assert.Equal((partialQty*partialPrice+(qty-partialQty)*finalPrice)/qty,result.AveragePrice);
        Assert.Equal(gross-250m,position.RealisedPnl);
        Assert.Equal(before,(position.RealisedPnl,position.CostsPaid,position.CurrentPrice));
        Assert.Equal(new[]{partialQty,qty-partialQty},result.Fills.Select(f=>f.Quantity));
    }

    [Theory]
    [InlineData(Direction.Buy,50)]
    [InlineData(Direction.Sell,-50)]
    public void SingleFullExitHasSameAverage(Direction side, decimal gross)
    {
        var result=WorkspaceExitBreakdownBuilder.Build(10,100,gross,side,[new("Final",10,105)]);
        Assert.True(result.Complete);
        Assert.Equal(105m,result.AveragePrice);
    }

    [Fact]
    public void MultiplePartialFillsAreWeightedByUnitsNotByNumberOfFills()
    {
        var result=WorkspaceExitBreakdownBuilder.Build(100,100,900,Direction.Buy,
            [new("Partial",20,120),new("Partial",30,110),new("Final",50,104)]);
        Assert.True(result.Complete);
        Assert.Equal(109m,result.AveragePrice);
    }

    [Fact]
    public void MissingDuplicatedOrMismatchedFillsNeverClaimAnAverage()
    {
        WorkspaceExitFill[][] cases = [[], [new("Final",5,105)],
            [new("Final",10,105),new("Final",10,105)], [new("Final",10,106)],
            [new("Partial",10,105)], [new("Final",10,0)]];
        foreach(var fills in cases)
        {
            var result=WorkspaceExitBreakdownBuilder.Build(10,100,50,Direction.Buy,fills);
            Assert.False(result.Complete);
            Assert.Null(result.AveragePrice);
        }
        Assert.Null(WorkspaceExitBreakdownBuilder.FromJournal("Partial","bad json"));
        Assert.Null(WorkspaceExitBreakdownBuilder.FromJournal("Partial","{\"quantity\":5}"));
    }
}
