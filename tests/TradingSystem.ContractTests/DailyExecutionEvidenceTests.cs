using TradingSystem.Application.Diagnostics;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Diagnostics;

namespace TradingSystem.ContractTests;

public sealed class DailyExecutionEvidenceTests
{
    [Fact]
    public void LiveRouteUsesOnlyTheCorrectMarketAndStateAtPaperFill()
    {
        var at = DateTimeOffset.UnixEpoch;
        var key = DailyLiveRouteHistory.Key("BANK-NIFTY");
        Assert.Equal(key,DailyLiveRouteHistory.Key("BANKNIFTY"));
        DailyLiveRouteHistory.Change[] history =
        [
            new(key,"LiveRouteBaseline:Off",at),
            new(DailyLiveRouteHistory.Key("NIFTY"),"LiveExecutionArmed",at),
            new(key,"LiveExecutionArmed",at.AddMinutes(2)),
            new(key,"LiveRouteBaseline:BuildDisabled",at.AddMinutes(10))
        ];
        Assert.Contains("No dated",DailyLiveRouteHistory.Explain("BANKNIFTY",at.AddSeconds(-1),history));
        Assert.Contains("OFF",DailyLiveRouteHistory.Explain("BANKNIFTY",at.AddMinutes(1),history));
        Assert.Contains("ON",DailyLiveRouteHistory.Explain("BANKNIFTY",at.AddMinutes(2),history));
        Assert.Contains("disabled",DailyLiveRouteHistory.Explain("BANKNIFTY",at.AddMinutes(10),history));
    }

    [Fact]
    public void WindowExpiryDoesNotOverwriteAWitnessedMissOrCreateAFictitiousCause()
    {
        var at = DateTimeOffset.UnixEpoch;
        var id = Guid.NewGuid();
        var missed = DailyExecutionEvidence.Parse(at,"LiveParityMissed:"+id.ToString("N"),"""{"reason":"Paper already closed"}""");
        var expired = new DailyExecutionEvidence.Observation(id,at.AddMinutes(5),"Discovery",null,null,
            Reason:TradingSystem.Infrastructure.Execution.AutomaticLiveExecutionService.ExpiredObservation);
        Assert.NotNull(missed);
        Assert.Equal(missed,DailyExecutionEvidence.EntryObservation([expired,missed!]));
        Assert.Contains("does not establish",DailyExecutionEvidence.EntryObservation([expired])!.Reason!);
    }

    [Fact]
    public void ReportsEarliestObservedDifferenceRatherThanLaterLossOrMissingFill()
    {
        var at = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        var trade = new DailyTradingTrade("NIFTY","paper",id,null,"NIFTYCE","test","Buy",65,
            100,110,at,at.AddMinutes(1),"Closed","Target",650,10,640,null,null,null,null);
        var intent = new LiveExecutionIntent(Guid.NewGuid(),"test",id,"NIFTY",Guid.NewGuid(),
            Direction.Buy,65,100,90,110,"ref",at.AddSeconds(2));
        var entry = DailyExecutionEvidence.Parse(at.AddSeconds(4),"LiveExecution:EntryFilledAndProtected",
            System.Text.Json.JsonSerializer.Serialize(new {sourceIdCompact=id.ToString("N"),details=new {FilledQuantity=65,AverageFillPrice=101m}}));
        Assert.NotNull(entry);
        var exit = new DailyExecutionEvidence.Observation(id,at.AddMinutes(2),"PaperExitFilled",105,65);
        var row = DailyExecutionEvidence.Review(trade,intent,[exit,entry!]);
        Assert.Contains("Entry price differed", row.Finding);
        Assert.Equal(entry!.At,row.ObservedAtUtc);
        Assert.Equal(2d,row.PaperToIntentSeconds);
        Assert.Null(row.IntentToSubmittedSeconds);
        Assert.Contains("No live intent",DailyExecutionEvidence.Review(trade,null,[]).Finding);
        var skip = DailyExecutionEvidence.Parse(at,"LiveParityException:"+id.ToString("N"),"""{"reason":"Research-only"}""");
        Assert.NotNull(skip);
        Assert.Contains("Research-only",DailyExecutionEvidence.Review(trade,null,[skip!]).Finding);
        var partial = new DailyExecutionEvidence.Observation(id,at.AddMinutes(1),"PartialExitOutcome",108,10,110,20);
        Assert.Contains("10 of 20",DailyExecutionEvidence.Review(trade,intent,[partial]).Finding);
        Assert.Contains("Incomplete",DailyExecutionEvidence.Review(trade,intent,[]).Finding);
    }
}
