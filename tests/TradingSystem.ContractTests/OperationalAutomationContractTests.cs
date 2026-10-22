using TradingSystem.Application.Diagnostics;
using TradingSystem.Infrastructure.Diagnostics;

namespace TradingSystem.ContractTests;

public sealed class OperationalAutomationContractTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 7, 0, 0, TimeSpan.Zero);

    [Fact]
    public void MiddayWatchExplainsWhyNoTradeHasOccurred()
    {
        var source = Report(new DailyTradingDiagnosticsSummary(
            18, 0, 18, new Dictionary<string, int> { ["EntryTimingRejected"] = 12, ["NoSignal"] = 6 },
            0, 0, 0, 0, 0m, 0m, 0m, Now.AddHours(-2), Now, Now));

        var result = OperationalAutomationService.BuildReport(
            new("NoTradeWatch:1200", new TimeOnly(12, 0)), source, true, null, Now);

        Assert.Equal("Attention", result.Status);
        Assert.True(result.ActionRequired);
        Assert.Equal("EntryTimingRejected", result.Metrics["dominantOutcome"]);
    }

    [Fact]
    public void EarlyWatchDoesNotEscalateAValidObservationWindow()
    {
        var source = Report(new DailyTradingDiagnosticsSummary(
            4, 0, 4, new Dictionary<string, int> { ["NoSignal"] = 4 },
            0, 0, 0, 0, 0m, 0m, 0m, Now.AddMinutes(-20), Now, Now));

        var result = OperationalAutomationService.BuildReport(
            new("NoTradeWatch:1030", new TimeOnly(10, 30)), source, true, null, Now);

        Assert.Equal("Observe", result.Status);
        Assert.False(result.ActionRequired);
    }

    [Fact]
    public void ReadinessReportsAuthenticationFailureWithoutExposingASecret()
    {
        var result = OperationalAutomationService.BuildReport(
            new("Readiness", new TimeOnly(9, 5)), Report(EmptySummary()),
            false, "GrowwApiException", Now);

        Assert.Equal("Attention", result.Status);
        Assert.True(result.ActionRequired);
        Assert.Contains(result.Findings, value => value.Contains("GrowwApiException"));
        Assert.DoesNotContain(result.Findings, value => value.Contains("token-value"));
    }

    [Fact]
    public void TelemetryAuditFlagsCompletedTradeWithoutProfitPathOrSignalLink()
    {
        var trade = new DailyTradingTrade("SENSEX", "MarketPaperPosition", Guid.NewGuid(), null,
            "SENSEX26OCTCE", "Core", "Buy", 200, 100m, 110m, Now.AddHours(-1), Now,
            "Target hit", "Target hit", 2_000m, 100m, 1_900m, null, null, null, null);
        var source = Report(EmptySummary() with { CompletedTradeCount = 1 }, [trade]);

        var result = OperationalAutomationService.BuildReport(
            new("TelemetryAudit", new TimeOnly(16, 5)), source, true, null, Now);

        Assert.Equal("Attention", result.Status);
        Assert.True(result.ActionRequired);
        Assert.Equal("1", result.Metrics["missingProfitPaths"]);
        Assert.Equal("1", result.Metrics["unlinkedTrades"]);
    }

    private static DailyTradingDiagnosticsReport Report(
        DailyTradingDiagnosticsSummary summary,
        IReadOnlyList<DailyTradingTrade>? trades = null) =>
        new(new DateOnly(2026, 10, 22), "NIFTY", Now, summary, [], [], trades ?? [], [], []);

    private static DailyTradingDiagnosticsSummary EmptySummary() =>
        new(0, 0, 0, new Dictionary<string, int>(), 0, 0, 0, 0,
            0m, 0m, 0m, null, null, null);
}
