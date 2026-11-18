using TradingSystem.Application.Diagnostics;

namespace TradingSystem.UnitTests;

public sealed class DailyDecisionClassificationTests
{
    [Theory]
    [InlineData("DecisionSnapshot:v2", DailyDecisionClassification.Kind.Informational)]
    [InlineData("PostExit5:abc", DailyDecisionClassification.Kind.Informational)]
    [InlineData("BankNiftyConfirmedTrail:abc", DailyDecisionClassification.Kind.Informational)]
    [InlineData("ExitAssessment:abc", DailyDecisionClassification.Kind.Informational)]
    [InlineData("PositionClosed:abc", DailyDecisionClassification.Kind.Informational)]
    [InlineData("SensexEarlyPullbackCandidate", DailyDecisionClassification.Kind.Informational)]
    [InlineData("EntryStage:EntryTimingRejected", DailyDecisionClassification.Kind.Mirror)]
    [InlineData("EntryStage:PaperPositionOpened", DailyDecisionClassification.Kind.Mirror)]
    [InlineData("NoSignal", DailyDecisionClassification.Kind.NoSetup)]
    [InlineData("EntryTimingRejected", DailyDecisionClassification.Kind.Rejected)]
    [InlineData("StructuralReentryRejected", DailyDecisionClassification.Kind.Rejected)]
    [InlineData("DataIncomplete", DailyDecisionClassification.Kind.Rejected)]
    [InlineData("PaperPositionOpened", DailyDecisionClassification.Kind.Accepted)]
    [InlineData("OptionOpenInterestAccepted", DailyDecisionClassification.Kind.Accepted)]
    [InlineData("NewUnknownOutcome", DailyDecisionClassification.Kind.Informational)]
    public void ClassifiesExplicitGateEventsOnly(string outcome, DailyDecisionClassification.Kind expected) =>
        Assert.Equal(expected, DailyDecisionClassification.Classify(outcome));

    [Theory]
    [InlineData(0, "Conditional break-even")]
    [InlineData(7000, "70% net-profit trail")]
    public void BankFloorReceiptDoesNotConfuseZeroFloorWithSeventyPercent(decimal floor, string expected)
    {
        var id=Guid.NewGuid();
        var receipt=DailyProtectionEvidence.Parse(DateTimeOffset.UtcNow,$"BankNiftyConfirmedTrail:{id:N}",
            System.Text.Json.JsonSerializer.Serialize(new{positionId=id,floorNet=floor,bid=461.25m,triggered=true}));
        Assert.NotNull(receipt);
        Assert.Equal(id,receipt.SourceId);
        Assert.Equal(expected,receipt.Evidence.Stage);
        Assert.Equal(floor,receipt.Evidence.FloorNet);
        Assert.True(receipt.Evidence.Triggered);
    }

    [Fact]
    public void NiftyReceiptUsesSignalAndRecordedEffectiveStop()
    {
        var id=Guid.NewGuid();
        var receipt=DailyProtectionEvidence.Parse(DateTimeOffset.UtcNow,$"ProfitProtection:{id:N}:202611181001",
            "{\"effectiveStop\":187.3,\"protectionStage\":\"InitialRisk\"}");
        Assert.NotNull(receipt);
        Assert.Equal(id,receipt.SourceId);
        Assert.Equal("InitialRisk",receipt.Evidence.Stage);
        Assert.Equal(187.3m,receipt.Evidence.StopPrice);
        Assert.False(receipt.Evidence.Triggered);
        Assert.Null(receipt.Evidence.FloorNet);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("not json")]
    public void InvalidUnlinkedReceiptsRemainUnavailable(string json) =>
        Assert.Null(DailyProtectionEvidence.Parse(DateTimeOffset.UtcNow,"ProfitProtection:invalid",json));

    [Fact]
    public void UsesExactObservationInsteadOfMinuteBucket()
    {
        var at = new DateTimeOffset(2026, 11, 18, 4, 53, 0, TimeSpan.Zero);
        var receipt = DailyProtectionEvidence.Parse(at, $"ProfitProtection:{Guid.NewGuid():N}",
            "{\"observedAtUtc\":\"2026-11-18T04:53:02Z\",\"effectiveStop\":184.05}");
        Assert.NotNull(receipt);
        Assert.Equal(at.AddSeconds(2), receipt.Evidence.AtUtc);
    }
}
