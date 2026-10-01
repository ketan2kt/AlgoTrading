using TradingSystem.Application.Execution;
using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class ResearchIntelligenceAnalyzerV3Tests
{
    [Fact]
    public void ProducesTimingMissedMoveRegimeCohortAndQualityEvidence()
    {
        var start = new DateTimeOffset(2026, 10, 1, 3, 45, 0, TimeSpan.Zero);
        var candles = Enumerable.Range(0, 40).Select(index =>
            new StrategyPriceBar(start.AddMinutes(index), 100m + index,
                101m + index, 99.5m + index, 100.8m + index)).ToArray();
        var state = new MarketStateAssessmentV2(IntradayMarketStateV2.BullTrend,
            Direction.Buy, .80m, .70m, .10m, .10m, .80m, .50m, 3.5m, []);
        var candidate = Snapshot(start.AddMinutes(20), 120.8m, Direction.Buy, state,
            [], 119m, 124m);
        var blocked = Snapshot(start.AddMinutes(10), 110.8m, null, state,
            ["Volume confirmation missing"], null, null);

        var report = ResearchIntelligenceAnalyzerV3.Analyze([candidate, blocked],
            [new(candidate, new CounterfactualOutcomeV2("TargetFirst", 2m, .2m, 4))],
            candles, 60);

        Assert.Equal("research-intelligence-v3", report.Version);
        Assert.Equal(1, report.EntryTiming.MatureMoveEntries);
        Assert.Equal(1, report.MissedMoves.NoCandidateSnapshots);
        Assert.Single(report.Cohorts);
        Assert.Equal("Healthy", report.DataQuality.Status);
        Assert.True(report.RegimeConfusion.Assessed > 0);
        Assert.True(report.Execution.ChargesAppliedToExecutedTrades);
        Assert.False(report.Execution.BidAskAndSlippageAvailable);
    }

    [Fact]
    public void FlagsMissingIntervalsAndNeverTreatsThemAsPromotionEvidence()
    {
        var start = new DateTimeOffset(2026, 10, 1, 3, 45, 0, TimeSpan.Zero);
        var candles = new[]
        {
            new StrategyPriceBar(start, 100m, 101m, 99m, 100m),
            new StrategyPriceBar(start.AddMinutes(3), 101m, 102m, 100m, 101m)
        };

        var report = ResearchIntelligenceAnalyzerV3.Analyze([], [], candles, 60);

        Assert.Equal(2, report.DataQuality.MissingIntervals);
        Assert.Equal("Incomplete", report.DataQuality.Status);
        Assert.Contains(report.Recommendations, value => value.Code == "DATA_QUALITY");
        Assert.Contains(report.Guardrails, value => value.Contains("never changes"));
    }

    private static DecisionFeatureSnapshotV2 Snapshot(DateTimeOffset time, decimal price,
        Direction? direction, MarketStateAssessmentV2 state, IReadOnlyList<string> failed,
        decimal? stop, decimal? target) => new("decision-dataset-v2", "sensex", time,
            price, "test-strategy", direction, .75m, state, "Candidate", price - 1m,
            price - 1m, price - 2m, 1m, 1.2m, price + 2m, price - 2m, stop, target,
            ["test evidence"], failed);
}
