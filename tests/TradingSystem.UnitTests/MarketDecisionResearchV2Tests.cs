using TradingSystem.Application.Execution;
using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class MarketDecisionResearchV2Tests
{
    [Fact]
    public void PromotionRequiresFullPaperEvidenceNotJustWinRate()
    {
        var decision = PaperPromotionPolicy.Evaluate(
            Report(trades: 199, sessions: 29, winRate: 90m, net: 10_000m, factor: 2m));
        Assert.False(decision.Eligible);
        Assert.Contains(decision.Reasons, reason => reason.Contains("200"));
        Assert.Contains(decision.Reasons, reason => reason.Contains("30"));
    }

    [Fact]
    public void PromotionRequiresEightyPercentPositiveNetAndProfitFactor()
    {
        Assert.False(PaperPromotionPolicy.Evaluate(Report(250, 35, 79.9m, 10_000m, 2m)).Eligible);
        Assert.False(PaperPromotionPolicy.Evaluate(Report(250, 35, 82m, -1m, 2m)).Eligible);
        Assert.False(PaperPromotionPolicy.Evaluate(Report(250, 35, 82m, 10_000m, 1.49m)).Eligible);
        Assert.True(PaperPromotionPolicy.Evaluate(Report(250, 35, 82m, 10_000m, 1.60m)).Eligible);
    }

    [Fact]
    public void CounterfactualUsesConservativeSameBarOrdering()
    {
        var outcome = CounterfactualOutcomeAnalyzerV2.Evaluate(Direction.Buy, 100m, 98m, 104m,
            [Bar(0, 100m, 105m, 97m, 103m)]);
        Assert.Equal("StopFirst", outcome.Outcome);
        Assert.Equal(2.5m, outcome.MaximumFavourableExcursionR);
    }

    [Fact]
    public void CalibrationMeasuresDifferenceBetweenPredictionAndObservedWins()
    {
        var bucket = Assert.Single(ProbabilityCalibrationAnalyzerV2.Analyze([
            (.82m, true), (.86m, false), (.84m, true), (.83m, true)
        ]));
        Assert.Equal(4, bucket.Observations);
        Assert.Equal(.75m, bucket.ActualWinRate);
        Assert.Equal(.0875m, bucket.CalibrationError);
    }

    [Fact]
    public void RangePlaybookRejectsMidpointEntries()
    {
        var bars = Enumerable.Range(0, 20).Select(index => Bar(index,
            100m, 101m, 99m, index % 2 == 0 ? 100.2m : 99.8m)).ToArray();
        var state = new MarketStateAssessmentV2(IntradayMarketStateV2.StructuredRange,
            null, .75m, .2m, .5m, .2m, .5m, .1m, 1m, []);
        Assert.Null(RangePlaybookV2.Evaluate(bars, state).Direction);
    }

    private static SelfImprovementResearchReport Report(int trades, int sessions,
        decimal winRate, decimal net, decimal factor) => new("v2", "nifty",
        new DateOnly(2026, 9, 29), sessions, 80m, winRate, false, trades,
        (int)(trades * winRate / 100m), net, 0m, factor, 0, 0, [], [], "Research",
        DateTimeOffset.UtcNow);

    private static StrategyPriceBar Bar(int minute, decimal open, decimal high,
        decimal low, decimal close) => new(DateTimeOffset.UtcNow.AddMinutes(minute),
        open, high, low, close);
}
