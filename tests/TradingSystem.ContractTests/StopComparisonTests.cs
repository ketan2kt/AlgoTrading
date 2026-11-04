using System.Text.Json;
using TradingSystem.Infrastructure.Diagnostics;

namespace TradingSystem.ContractTests;

public sealed class StopComparisonTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch;
    private static readonly Guid Id = Guid.Parse("88888888-0000-0000-0000-000000000001");

    [Fact]
    public void ReportsSupersededRevisionWrongQuantityAndEventualMatchingConfirmation()
    {
        StopComparison.Point[] points = [
            new(Id,At,StopComparison.Kind.Desired,110,100),
            new(Id,At.AddSeconds(1),StopComparison.Kind.Desired,112,50),
            new(Id,At.AddSeconds(2),StopComparison.Kind.Ack,112,100,"stop"),
            new(Id,At.AddSeconds(3),StopComparison.Kind.Ack,112,50,"stop",At.AddSeconds(2.25)),
            new(Id,At.AddSeconds(4),StopComparison.Kind.Fill,109,50,"stop"),
            new(Guid.NewGuid(),At.AddSeconds(1),StopComparison.Kind.Ack,110,100,"other")
        ];
        var rows = StopComparison.Review(Id,points.Reverse());
        Assert.Equal(4,rows.Count);
        Assert.Contains("Superseded",rows[0].Finding);
        Assert.Contains("differs",rows[1].Finding);
        Assert.Equal(2d,rows[2].SecondsToConfirmation);
        Assert.Equal(1.25d,rows[2].SecondsBeforeCall);
        Assert.Equal(0.75d,rows[2].SecondsInCallAndVerification);
        Assert.Equal(109m,rows[3].FillPrice);
        Assert.Equal(112m,rows[3].BrokerStop);
        Assert.Contains("below",rows[3].Finding);
    }

    [Fact]
    public void CannotInferProtectionFromAnUnrelatedOrderOrFromAnOldRequest()
    {
        var legacy = JsonSerializer.Serialize(new {sourceIdCompact=Id.ToString("N"),details=new {requestedStop=110}});
        Assert.Empty(StopComparison.Parse(At,"LiveExecution:StopModified",legacy));
        var row = Assert.Single(StopComparison.Review(Id,[
            new(Id,At,StopComparison.Kind.Ack,110,100,"original"),
            new(Id,At.AddSeconds(1),StopComparison.Kind.Fill,107,50,"residual")
        ]), r => r.FillPrice.HasValue);
        Assert.Null(row.BrokerStop);
        Assert.Null(row.SecondsToConfirmation);
    }

    [Theory]
    [InlineData("RecoveredProtection")]
    [InlineData("FinalExitResidualProtected")]
    public void ARecoveredStopIsComparableButAnUncertainStateBreaksFillAttribution(string name)
    {
        var ack = JsonSerializer.Serialize(new {sourceIdCompact=Id.ToString("N"),details=new {
            verifiedStop=new {BrokerProtectionId="residual",StopLoss=108m,Quantity=10,UpdatedAtUtc=At.AddSeconds(2)}
        }});
        var points = StopComparison.Parse(At.AddSeconds(3),"LiveExecution:"+name,ack).Concat([
            new StopComparison.Point(Id,At,StopComparison.Kind.Desired,108,10),
            new StopComparison.Point(Id,At.AddSeconds(4),StopComparison.Kind.Uncertain,null,null),
            new StopComparison.Point(Id,At.AddSeconds(5),StopComparison.Kind.Fill,106,10,"residual")
        ]);
        var rows = StopComparison.Review(Id,points);
        Assert.Equal(2d,rows[0].SecondsToConfirmation);
        Assert.Null(rows[2].BrokerStop);
        Assert.Equal(106m,rows[2].FillPrice);
    }

    [Theory]
    [InlineData("LiveProtectionDesired:","effectiveStop")]
    [InlineData("PaperStopSize:","effectiveStop")]
    [InlineData("TrailingStop:","newStop")]
    public void UsesObservedQuantityAndConfirmationReadTime(string outcome,string field)
    {
        var paper = JsonSerializer.Serialize(new Dictionary<string,int>{{field,110},{"remainingQuantity",50}});
        var live = JsonSerializer.Serialize(new {sourceIdCompact=Id.ToString("N"),details=new {
            reservedStop=new {BrokerProtectionId="order",StopLoss=110m,Quantity=50,UpdatedAtUtc=At.AddSeconds(2)}
        }});
        var points = StopComparison.Parse(At,outcome+Id.ToString("N"),paper)
            .Concat(StopComparison.Parse(At.AddSeconds(9),"LiveExecution:PartialExitOutcome",live));
        var match = Assert.Single(StopComparison.Review(Id,points));
        Assert.Equal(2d,match.SecondsToConfirmation);
        Assert.Equal(50,match.BrokerQuantity);
    }
}
