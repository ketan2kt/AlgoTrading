using Microsoft.EntityFrameworkCore;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.MarketData;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.ContractTests;

public sealed class WorkspaceCompletedPnlTests
{
    [Theory]
    [InlineData("nifty")]
    [InlineData("sensex")]
    [InlineData("bank-nifty")]
    public void IncludesClosedHeroZeroButNotOpenOrPartialBookings(string market)
    {
        var start=new DateTimeOffset(2026,11,18,18,30,0,TimeSpan.Zero);var end=start.AddDays(1);
        MarketPaperPosition New(string owner,string strategy,DateTimeOffset opened) => new(
            Guid.NewGuid(),owner,Guid.NewGuid(),Guid.NewGuid(),strategy,Direction.Buy,100,100,90,120,opened);
        var win=New(market,"Hero Zero|a|CE",start);win.Close(110,25,"Closed",start.AddHours(1));
        var loss=New(market,"Hero Zero|a|PE",start);loss.Close(95,25,"Closed",start.AddHours(1));
        var partial=New(market,"Hero Zero|b|CE",start);partial.TakePartial(120,40,20,start.AddHours(1));
        var open=New(market,"Hero Zero|b|PE",start);open.Mark(150,95,start.AddHours(1));
        var yesterday=New(market,"Hero Zero|old|CE",start.AddDays(-1));yesterday.Close(200,0,"Closed",start.AddTicks(-1));
        var tomorrow=New(market,"Hero Zero|next|CE",start);tomorrow.Close(200,0,"Closed",end);
        var foreign=New("other","Hero Zero|x|CE",start);foreign.Close(200,0,"Closed",start.AddHours(1));
        var rows=new[]{win,loss,partial,open,yesterday,tomorrow,foreign};
        var closed=EfTradingWorkspaceReader.CompletedPositionRows(rows.AsQueryable(),market,start,end).ToArray();
        Assert.Equal(2,closed.Length);Assert.Equal(450m,closed.Sum(p=>p.RealisedPnl));
        partial.Close(105,30,"Closed",start.AddHours(2));
        Assert.Equal(1500m,EfTradingWorkspaceReader.CompletedPositionRows(rows.AsQueryable(),market,start,end).Sum(p=>p.RealisedPnl));
    }

    [Fact]
    public void PostgreSqlPredicateIsTranslatedAndNiftyLegacyCannotDoubleCount()
    {
        var at=new DateTimeOffset(2026,11,19,4,0,0,TimeSpan.Zero);
        var legacy=new MarketPaperPosition(Guid.NewGuid(),"nifty",Guid.NewGuid(),Guid.NewGuid(),
            "NiftyAutomation",Direction.Buy,100,100,90,120,at);
        legacy.Close(110,10,"Closed",at.AddMinutes(1));
        Assert.Empty(EfTradingWorkspaceReader.CompletedPositionRows(new[]{legacy}.AsQueryable(),"nifty",at,at.AddDays(1)));
        using var db=new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused").Options,TimeProvider.System);
        var query=EfTradingWorkspaceReader.CompletedPositionRows(db.MarketPaperPositions,"nifty",at,at.AddDays(1))
            .GroupBy(_=>1).Select(g=>new {Net=g.Sum(p=>p.RealisedPnl),Count=g.Count()});
        var sql=query.ToQueryString(); // Translation only; no database or credentials accessed.
        Assert.Contains("sum(",sql,StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ClosedAtUtc",sql,StringComparison.Ordinal);
    }
}
