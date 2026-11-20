using TradingSystem.Application.Broker;
using TradingSystem.Application.Diagnostics;
using TradingSystem.Application.Execution;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Diagnostics;

namespace TradingSystem.ContractTests;

public sealed class ReplayBookCoverageAuditTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 20, 9, 15, 0, TimeSpan.FromHours(5.5));

    [Theory]
    [InlineData("NIFTY")]
    [InlineData("SENSEX")]
    [InlineData("BANKNIFTY")]
    public void BackupCoversOnlyTheMatchingContractAndDoesNotEraseStrictGate(string market)
    {
        var slice = Slice(market, [Book(market + "CE", 0), Book(market + "CE", 30, true), Book(market + "PE", 0)]);
        var coverage = ReplayBookCoverageAudit.Read(slice);
        var call = Assert.Single(coverage, value => value.Symbol == market + "CE");
        Assert.Equal(30, call.PrimaryMissingSeconds);
        Assert.Equal(0, call.CombinedMissingSeconds);
        var put = Assert.Single(coverage, value => value.Symbol == market + "PE");
        Assert.Equal(30, put.CombinedMissingSeconds);
        Assert.Equal(Start.AddSeconds(30), put.FirstUncoveredAtUtc);
        var strict = Report() with { ActionRequired = true, Status = "Attention", Findings = ["Missing pre-open receipt"] };
        var report = OperationalAutomationService.AddBookCoverage(strict, coverage);
        Assert.Equal("1", report.Metrics["contractsHelpedByBackup"]);
        Assert.Equal("30", report.Metrics["backupCoveredContractSeconds"]);
        Assert.Equal("30", report.Metrics["combinedUncoveredContractSeconds"]);
        Assert.Contains("Missing pre-open receipt", report.Findings);
        Assert.True(report.ActionRequired);
    }

    [Fact]
    public void StaleCrossedEmptyAndFutureDatedBooksNeverHideGaps()
    {
        var slice = Slice("NIFTY", [
            Book("NIFTYCE", 0) with { SourceAtUtc = Start.AddSeconds(-31) },
            Book("NIFTYCE", 0) with { DepthSourceAtUtc = Start.AddSeconds(-31) },
            Book("NIFTYCE", 0, true) with { SourceAtUtc = Start.AddSeconds(1) },
            Book("NIFTYCE", 0, true) with { OfferQuantity = 0 },
            Book("NIFTYCE", 0, true) with { BidPrice = 102, OfferPrice = 101 },
            Book("NIFTYCE", 0, true) with { Source = "CandleCloseProxy" }]);
        Assert.All(ReplayBookCoverageAudit.Read(slice), value => Assert.Equal(60, value.CombinedMissingSeconds));
    }

    [Fact]
    public void SourceAgeBoundsCoverageAndOverlapsAreNotCountedTwice()
    {
        var rows = new[] { Book("NIFTYCE", 0), Book("NIFTYCE", 10, true),
            Book("NIFTYCE", 35, true) with { SourceAtUtc = Start.AddSeconds(20) } };
        var coverage = ReplayBookCoverageAudit.Read(Slice("NIFTY", rows));
        var call = Assert.Single(coverage, value => value.Symbol == "NIFTYCE");
        Assert.Equal(10, call.CombinedMissingSeconds);
        Assert.Equal(Start.AddSeconds(50), call.FirstUncoveredAtUtc);
    }

    [Fact]
    public void UnobservedPlannedContractsCannotDisappearFromAudit()
    {
        var coverage = ReplayBookCoverageAudit.Read(Slice("SENSEX", []));
        Assert.Equal(2, coverage.Count);
        Assert.All(coverage, value => Assert.Equal(60, value.CombinedMissingSeconds));
        Assert.True(OperationalAutomationService.AddBookCoverage(Report(), coverage).ActionRequired);
        Assert.True(OperationalAutomationService.AddBookCoverage(Report(), []).ActionRequired);
    }

    [Fact]
    public void BackupCompletionCannotTurnMissingPolicyOrControlsGreen()
    {
        var original = Report() with { Status = "Attention", ActionRequired = true, Findings = ["Missing controls"] };
        var report = OperationalAutomationService.AddBookCoverage(original, [new("NIFTYCE", 60, 0, null)]);
        Assert.True(report.ActionRequired);
        Assert.Equal("Attention", report.Status);
        Assert.Contains("Missing controls", report.Findings);
    }

    [Fact]
    public void FailedAuditRetriesAreBoundedAndHealthyAuditIsNotRepeated()
    {
        Assert.True(OperationalAutomationService.ReplayAuditDue(0, null, false, Start));
        Assert.False(OperationalAutomationService.ReplayAuditDue(1, Start, false, Start.AddMinutes(29)));
        Assert.True(OperationalAutomationService.ReplayAuditDue(1, Start, false, Start.AddMinutes(30)));
        Assert.True(OperationalAutomationService.ReplayAuditDue(2, Start, false, Start.AddMinutes(30)));
        Assert.False(OperationalAutomationService.ReplayAuditDue(3, Start, false, Start.AddHours(2)));
        Assert.False(OperationalAutomationService.ReplayAuditDue(1, Start, true, Start.AddHours(2)));
    }

    private static HistoricalReplayQuote Book(string symbol, int second, bool backup = false) => new(symbol, Start.AddSeconds(second), 100)
    {
        Source = backup ? HistoricalQuoteObservation.UpstoxSnapshot : "GrowwFeedSample",
        SourceAtUtc = Start.AddSeconds(second), DepthSourceAtUtc = backup ? null : Start.AddSeconds(second),
        BidPrice = 99, OfferPrice = 101, BidQuantity = 100, OfferQuantity = 100
    };

    private static HistoricalReplayArchiveSlice Slice(string market, IReadOnlyList<HistoricalReplayQuote> quotes) =>
        new(new(new(2026, 11, 20), market, [], quotes, Start, Start.AddMinutes(1)), 100, null, [], [])
        {
            RecordedContractUniverse = new("test", [
                new(market, "NSE", "FNO", "1", market + "CE", InstrumentType.CallOption, new(2026, 11, 27), 100, 1, .05m),
                new(market, "NSE", "FNO", "2", market + "PE", InstrumentType.PutOption, new(2026, 11, 27), 100, 1, .05m)])
        };

    private static OperationalAutomationReport Report() => new("test", "nifty", new(2026, 11, 20), "ReplayInputAudit",
        "Healthy", "Inputs ready", new Dictionary<string, string>(), [], false, Start.AddHours(7));
}
