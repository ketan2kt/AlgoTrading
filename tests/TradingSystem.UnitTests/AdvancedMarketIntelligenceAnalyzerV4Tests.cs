using TradingSystem.Application.Execution;
using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class AdvancedMarketIntelligenceAnalyzerV4Tests
{
    [Fact]
    public void CombinesLegalMarketInputsWithoutActivatingResearchSizing()
    {
        var start = new DateTimeOffset(2026, 10, 1, 3, 45, 0, TimeSpan.Zero);
        var bars = Enumerable.Range(0, 45).Select(index => new StrategyPriceBar(
            start.AddMinutes(index), 100m + index, 101m + index, 99m + index,
            100.7m + index, 1_000m + index * 10m)).ToArray();
        var peer = Enumerable.Range(0, 45).Select(index => new StrategyPriceBar(
            start.AddMinutes(index), 200m + index, 201m + index, 199m + index,
            200.5m + index, 2_000m)).ToArray();
        var state = new MarketStateAssessmentV2(IntradayMarketStateV2.BullTrend,
            Direction.Buy, .75m, .65m, .10m, .05m, .80m, .40m, 1.2m, []);
        var decision = new DecisionFeatureSnapshotV2("decision-dataset-v2", "nifty",
            start.AddMinutes(44), 144.7m, "trend", Direction.Buy, .76m, state,
            "Candidate", 140m, 143m, 139m, 1m, 1.2m, 142m, 138m, 141m, 150m, [], []);
        var options = new[]
        {
            new OptionResearchPointV4(145m, true, 10m, 5_000m, 2_000m, .5m, .1m, -.2m, .3m, 15m),
            new OptionResearchPointV4(145m, false, 9m, 6_000m, 2_500m, -.5m, .1m, -.2m, .3m, 17m)
        };

        var report = AdvancedMarketIntelligenceAnalyzerV4.Analyze(bars, bars, peer,
            options, [], [decision], [], [new("TEST", "Low", "finding", "experiment", 10)],
            start.AddMinutes(44));

        Assert.True(report.Options.Available);
        Assert.Equal("Confirmed", report.CrossMarket.Alignment);
        Assert.True(report.VolumeProfile.Available);
        Assert.Contains(report.Levels, value => value.Kind == "Round number");
        Assert.Equal("ShadowOnly", report.AdaptiveRisk.Verdict);
        Assert.Single(report.Experiments);
        Assert.Contains(report.Capabilities, value =>
            value.Capability == "Constituent breadth" && value.Status == "Unavailable");
    }

    [Fact]
    public void AbstainsWhenCriticalContextIsUnavailable()
    {
        var start = new DateTimeOffset(2026, 10, 1, 3, 45, 0, TimeSpan.Zero);
        var bars = Enumerable.Range(0, 20).Select(index => new StrategyPriceBar(
            start.AddMinutes(index), 100m, 101m, 99m, 100m)).ToArray();

        var report = AdvancedMarketIntelligenceAnalyzerV4.Analyze(bars, [], [], [], [],
            [], [], [], start);

        Assert.False(report.Options.Available);
        Assert.False(report.CrossMarket.Available);
        Assert.False(report.VolumeProfile.Available);
        Assert.Equal("NoCandidate", report.MetaLabel.Verdict);
        Assert.Equal("ShadowOnly", report.AdaptiveRisk.Verdict);
    }
}
