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

    [Fact]
    public void PaperOnlyTradeLinksAnUnambiguousCandidateWithoutClaimingAFill()
    {
        const string symbol = "BANKNIFTYCE";
        HistoricalReplayEntryEvidence[] evidence =
        [new(symbol, At.AddSeconds(-4), At.AddSeconds(-4), "Candidate", 10),
         new(symbol, At.AddSeconds(-4), At.AddSeconds(90), "Entry expired", 10, "No eligible fill")];
        var result = PaperSessionFidelity.Compare(new([Paper(symbol)], []), [], evidence);
        var row = Assert.Single(result.Rows);
        Assert.Equal("Paper only", row.Finding);
        Assert.Equal("Entry expired", row.EntryEvidence!.Stage);
        Assert.Null(row.Replay);
        Assert.Equal(0, result.ReplayTrades);
    }

    [Fact]
    public void MatchingDecisionAndModeledEntryAreDistinguishedFromAClosedTrade()
    {
        const string symbol = "BANKNIFTYCE";
        HistoricalReplayEntryEvidence[] evidence =
        [new(symbol, At, At, "Candidate", 10) { Strategy = "normal" },
         new(symbol, At, At.AddSeconds(1), "Entry filled", 10),
         new(symbol, At, At.AddMinutes(3), "Exit unsupported", 10)];
        var result = PaperSessionFidelity.Compare(new([Paper(symbol)], []), [], evidence);
        var row = Assert.Single(result.Rows);
        Assert.Equal("Same entry contract and strategy", row.DecisionEvidence);
        Assert.Equal("Modeled entry: 10 units; no verified closed replay trade", row.ExecutionEvidence);
        Assert.Equal(0, result.ReplayTrades);
    }

    [Fact]
    public void PaperStopLossAndReplayObservedStopAreTheSameExitReason()
    {
        var paper = Paper("NIFTYCE");
        var stopped = paper with { Actions = [paper.Entry, paper.Actions[1] with { Reason = "StopLoss" }] };
        var result = PaperSessionFidelity.Compare(new([stopped], []), [Fill(paper.Symbol) with { ExitReason = "ObservedStop" }]);
        Assert.Equal("Recorded actions match", result.Verdict);
    }

    [Fact]
    public void KnownDecisionCandleOverridesNearbyFillMatching()
    {
        var paper = Paper("BANKNIFTYCE") with { DecisionBarCloseUtc = At.AddMinutes(-1),
            EvaluationStartedAtUtc = At.AddSeconds(-55) };
        var wrong = Fill(paper.Symbol) with { EntryAtUtc = At.AddSeconds(2) };
        var right = Fill(paper.Symbol) with { DecisionAtUtc = At.AddMinutes(-1), EntryAtUtc = At.AddMinutes(2) };
        var result = PaperSessionFidelity.Compare(new([paper], []), [wrong, right]);
        Assert.Contains(result.Rows, row => row.Finding == "Time differs" && row.Replay?.At == right.EntryAtUtc &&
            row.PaperEvaluationStartedAtUtc == At.AddSeconds(-55));
        Assert.Contains(result.Rows, row => row.Finding == "Replay only" && row.Replay?.At == wrong.EntryAtUtc);
    }
}
