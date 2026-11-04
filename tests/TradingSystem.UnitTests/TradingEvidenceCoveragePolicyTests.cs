using TradingSystem.Application.Diagnostics;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class TradingEvidenceCoveragePolicyTests
{
    private static readonly DateOnly Date = new(2026, 11, 4);
    private static readonly DateTimeOffset At = new(2026, 11, 4, 7, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CandlesWithoutAnyRegularEvidenceAreNotReportedHealthy()
    {
        Assert.StartsWith("Missing normal-engine evidence",
            TradingEvidenceCoveragePolicy.Warning("nifty", Date, null, null, At));
    }

    [Fact]
    public void ARecordedPrerequisiteBlockMakesTheNoTradeReasonTraceable()
    {
        Assert.Null(TradingEvidenceCoveragePolicy.Warning("nifty", Date,
            null, At, At));
    }

    [Fact]
    public void AStalledPreviouslyActiveEngineIsAlsoFlagged()
    {
        Assert.StartsWith("Regular-engine evidence gap",
            TradingEvidenceCoveragePolicy.Warning("sensex", Date,
                At.AddMinutes(-8), At.AddMinutes(-7), At));
    }

    [Fact]
    public void EvidenceCheckDoesNotRequireEntryDecisionsAfterEntryCutoff()
    {
        var cutoff = new DateTimeOffset(2026, 11, 4, 9, 30, 0, TimeSpan.Zero);
        Assert.Null(TradingEvidenceCoveragePolicy.Warning("bank-nifty", Date,
            cutoff, null, cutoff.AddHours(2)));
    }

    [Fact]
    public void StructuredEvidenceIsPreservedWithItsOriginalEventTimestamp()
    {
        var item = new SystemEvent(Guid.NewGuid(), "Information", "EngineEvidence.nifty.regular",
            "No trade: futures unavailable", At, "{\"futureCandles\":0}");
        Assert.Equal("{\"futureCandles\":0}", item.DetailsJson);
        Assert.Equal(At, item.OccurredAtUtc);
    }
}
