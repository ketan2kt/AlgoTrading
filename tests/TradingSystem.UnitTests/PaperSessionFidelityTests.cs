using TradingSystem.Application.Execution;

namespace TradingSystem.UnitTests;

public sealed class PaperSessionFidelityTests
{
    private static readonly DateTimeOffset At = new(2026,11,5,4,0,0,TimeSpan.Zero);
    private static HistoricalReplayFill Fill(string symbol) => new(symbol,"normal",At,At,At.AddMinutes(2),100,103,10,30,2,28,"ObservedTarget")
        {EntryUnits=10,OriginalEntryPremium=100};
    private static FidelityTrade Paper(string symbol,decimal entry=100) => new(symbol,[
        new("Entry",At,10,entry,"normal"),new("Exit",At.AddMinutes(2),10,103,"TargetHit")
    ]);

    [Fact]
    public void EqualDecisionsAndDifferentPremiumAreNotADifferentTrade()
    {
        var r=PaperSessionFidelity.Compare(new([Paper("NIFTYCE",101)],[]),[Fill("NIFTYCE")]);
        Assert.Equal("Same actions; modeled fill prices differ",r.Verdict);
        Assert.Equal("Entry",r.FirstDifference!.Kind);
        Assert.Equal("Fill price only",r.FirstDifference.Finding);
    }

    [Fact]
    public void AddsAndPartialsStayWithTheOriginalPositionAndTimeIsNotRoundedAway()
    {
        var terminal=Fill("BANKNIFTYPE") with {AddOnAt=At.AddSeconds(10),AddOnUnits=2,AddOnPremium=101,
            Quantity=9,OriginalQuantity=12};
        var partial=terminal with {ExitAtUtc=At.AddMinutes(1),Quantity=3,IsPartialExit=true};
        FidelityAction[] actions=[new("Entry",At,10,100,"normal"),new("Add-on",At.AddSeconds(11),2,101),
            new("Partial",At.AddMinutes(1),3,103),new("Exit",At.AddMinutes(2),9,103,"TargetHit")];
        var r=PaperSessionFidelity.Compare(new([new(terminal.Symbol,actions)],[]),[partial,terminal]);
        Assert.Equal("Add-on",r.FirstDifference!.Kind);
        Assert.Equal("Time differs",r.FirstDifference.Finding);
        Assert.Equal(4,r.Rows.Count);
    }

    [Fact]
    public void OneSidedAndAmbiguousEntriesAreNeverForcedTogether()
    {
        var r=PaperSessionFidelity.Compare(new([Paper("NIFTYCE"),Paper("SENSEXPE")],[]),
            [Fill("NIFTYCE"),Fill("NIFTYCE") with {EntryAtUtc=At.AddSeconds(20)},Fill("BANKNIFTYPE")]);
        Assert.Equal("Inconclusive",r.Verdict);
        Assert.Contains(r.Rows,x=>x.Symbol=="SENSEXPE" && x.Finding=="Paper only");
        Assert.Contains(r.Rows,x=>x.Symbol=="BANKNIFTYPE" && x.Finding=="Replay only");
        Assert.DoesNotContain(r.Rows,x=>x.Paper!=null && x.Replay!=null);
    }
}
