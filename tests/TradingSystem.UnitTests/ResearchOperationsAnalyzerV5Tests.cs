using TradingSystem.Application.Execution;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class ResearchOperationsAnalyzerV5Tests
{
    [Fact]
    public void ProducesReplayComparisonSearchSimulationProvenanceAndGovernance()
    {
        var start = new DateTimeOffset(2026, 10, 1, 3, 45, 0, TimeSpan.Zero);
        var candidates = Enumerable.Range(0, 40).Select(index =>
        {
            var snapshot = Snapshot(start.AddMinutes(index), index % 2 == 0 ? .76m : .66m,
                index % 3 == 0 ? 2.5m : 1.5m);
            var outcome = new CounterfactualOutcomeV2(index % 4 == 0 ? "StopFirst" : "TargetFirst",
                index % 4 == 0 ? .3m : 1.2m, index % 4 == 0 ? 1m : .2m, 3);
            return new ResearchCandidateV3(snapshot, outcome);
        }).ToArray();
        var intelligence = Intelligence(candidates.Length);

        var report = ResearchOperationsAnalyzerV5.Analyze(
            candidates.Select(value => value.Snapshot).ToArray(), [], candidates,
            intelligence, "1.0+abc", "ABC123");

        Assert.Equal(40, report.ReplayFrames.Count);
        Assert.NotEmpty(report.StrategyComparison);
        Assert.Equal(9, report.ParameterSearch.Count);
        Assert.Equal(2_000, report.MonteCarlo.Simulations);
        Assert.Equal("1.0+abc", report.Provenance.BuildVersion);
        Assert.Equal("OfflineReplay", report.Governance.CurrentStage);
        Assert.Contains("JSON", report.Retention.ExportFormats);
    }

    [Fact]
    public void AlertsWhenNoDecisionsExist()
    {
        var report = ResearchOperationsAnalyzerV5.Analyze([], [], [], Intelligence(0),
            "test", "checksum");

        Assert.Contains(report.Alerts, value => value.Code == "NO_DECISIONS" &&
            value.Severity == "Critical");
        Assert.False(report.Governance.AdvancementPermitted);
    }

    private static DecisionFeatureSnapshotV2 Snapshot(DateTimeOffset time,
        decimal confidence, decimal maturity)
    {
        var state = new MarketStateAssessmentV2(IntradayMarketStateV2.BullTrend,
            Direction.Buy, .8m, .7m, .1m, .1m, .8m, .4m, maturity, []);
        return new("decision-dataset-v2", "nifty", time, 100m, "trend-v1",
            Direction.Buy, confidence, state, "Triggered", 99m, 100m, 98m,
            1m, 1.2m, 102m, 98m, 98m, 104m, ["evidence"], []);
    }

    private static ResearchIntelligenceV3 Intelligence(int candidates) => new(
        "research-intelligence-v3",
        new(candidates, 0, 0, 0, 1m, 1m, .2m, new Dictionary<string, decimal>()),
        new(0, 0, new Dictionary<string, int>()),
        new(40, 32, 8, .8m, new Dictionary<string, int>()),
        [new("trend-v1", "BullTrend", "Buy", "Opening", candidates,
            candidates * 3 / 4, .75m, 1.2m, .2m)],
        new(candidates, candidates * 3 / 4, candidates / 4, 5, 2, 1.2m, .2m),
        new(true, false, false, "test"),
        new(100, 60, 0, 0, 0, 1m, "Healthy"),
        [], ["guardrail"]);
}
