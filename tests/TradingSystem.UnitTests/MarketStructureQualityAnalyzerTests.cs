using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class MarketStructureQualityAnalyzerTests
{
    [Fact]
    public void RejectsAlternatingLowEfficiencyChop()
    {
        var bars = Bars([100m, 102m, 99m, 103m, 98m, 102m, 99m, 103m, 98m, 102m, 99m, 101m]);

        var result = MarketStructureQualityAnalyzer.Analyze(bars, Direction.Buy, 101m, 100.5m,
            1m, 104m, 97m, 2m);

        Assert.Equal(MarketStructureQualityState.NoisyChop, result.State);
        Assert.False(result.WouldPermit);
        Assert.True(result.ChopScore >= 0.62m);
    }

    [Fact]
    public void PermitsEfficientDevelopingTrendWithRoom()
    {
        var bars = Bars([100m, 100.5m, 101m, 101.4m, 101.8m, 102.2m, 102.6m,
            103m, 103.4m, 103.8m, 104.2m, 104.6m]);

        var result = MarketStructureQualityAnalyzer.Analyze(bars, Direction.Buy, 104.6m, 102m,
            2m, 110m, 98m, 1m);

        Assert.True(result.State is MarketStructureQualityState.CleanTrend or
            MarketStructureQualityState.DevelopingTrend);
        Assert.True(result.WouldPermit);
        Assert.True(result.TrendEfficiency > 0.80m);
    }

    [Fact]
    public void RejectsMatureContinuationEvenWhenTrendIsClean()
    {
        var bars = Bars([100m, 101m, 102m, 103m, 104m, 105m, 106m, 107m, 108m, 109m, 110m, 111m]);

        var result = MarketStructureQualityAnalyzer.Analyze(bars, Direction.Buy, 111m, 105m,
            1m, 115m, 99m, 1m);

        Assert.Equal(MarketStructureQualityState.MatureTrend, result.State);
        Assert.False(result.WouldPermit);
    }

    [Fact]
    public void RejectsCandidateWithInsufficientKnownRoom()
    {
        var bars = Bars([100m, 100.3m, 100.6m, 100.9m, 101.2m, 101.5m, 101.8m,
            102.1m, 102.4m, 102.7m, 103m, 103.3m]);

        var result = MarketStructureQualityAnalyzer.Analyze(bars, Direction.Buy, 103.3m, 102m,
            2m, 103.8m, 98m, 1m);

        Assert.False(result.WouldPermit);
        Assert.True(result.RoomToRiskRatio < 1.10m);
    }

    private static StrategyPriceBar[] Bars(decimal[] closes)
    {
        var start = DateTimeOffset.UtcNow.AddMinutes(-closes.Length * 5);
        return closes.Select((close, index) => new StrategyPriceBar(start.AddMinutes(index * 5),
            close - 0.1m, close + 0.2m, close - 0.2m, close)).ToArray();
    }
}
