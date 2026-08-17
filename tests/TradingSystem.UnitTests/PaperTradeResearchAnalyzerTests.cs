using TradingSystem.Application.Execution;

namespace TradingSystem.UnitTests;

public sealed class PaperTradeResearchAnalyzerTests
{
    [Fact]
    public void IdentifiesMaterialProfitGiveback()
    {
        var result = PaperTradeResearchAnalyzer.Analyze(new(100, 50m, 51m,
            100m, 20m, 80m, "UnderlyingTrendInvalidated",
            [50m, 55m, 54m, 51m], []));

        Assert.Equal(500m, result.MaximumFavourableExcursion);
        Assert.Equal(400m, result.ProfitGiveback);
        Assert.Equal(0.20m, result.CapturedProfitRatio);
        Assert.Equal("MaterialProfitGiveback", result.Assessment);
    }

    [Fact]
    public void IdentifiesPotentialEarlyExitFromFollowUpEvidence()
    {
        var result = PaperTradeResearchAnalyzer.Analyze(new(100, 50m, 49m,
            -100m, 20m, -120m, "UnderlyingTrendInvalidated",
            [50m, 49m, 48m], [50m, 350m, 200m]));

        Assert.Equal(350m, result.BestPostExitIncrementalPnl);
        Assert.Equal("PotentialEarlyExit", result.Assessment);
    }

    [Fact]
    public void RequiresEnoughPricePathBeforeJudgingExit()
    {
        var result = PaperTradeResearchAnalyzer.Analyze(new(65, 100m, 101m,
            65m, 10m, 55m, "TargetReached", [], []));

        Assert.Equal("InsufficientPricePath", result.Assessment);
    }

    [Fact]
    public void CalculatesProfitFactor()
    {
        Assert.Equal(1.5m, PaperTradeResearchAnalyzer.ProfitFactor([100m, 50m, -100m]));
        Assert.Equal(999m, PaperTradeResearchAnalyzer.ProfitFactor([100m, 50m]));
        Assert.Equal(0m, PaperTradeResearchAnalyzer.ProfitFactor([]));
    }
}
