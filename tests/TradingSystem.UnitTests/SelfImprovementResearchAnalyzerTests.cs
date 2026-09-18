using TradingSystem.Application.Execution;

namespace TradingSystem.UnitTests;

public sealed class SelfImprovementResearchAnalyzerTests
{
    [Fact]
    public void TracksSeventyPercentTradeTargetAndKeepsChangesInShadowMode()
    {
        var start = new DateOnly(2026, 9, 1);
        var trades = Enumerable.Range(0, 10).SelectMany(day => Enumerable.Range(0, 4).Select(index =>
            new ResearchTradeObservation(start.AddDays(day), "range", "Late",
                index < 3 ? 500m : -300m, 50m))).ToArray();

        var report = SelfImprovementResearchAnalyzer.Analyze("sensex", start.AddDays(9), trades,
            [], DateTimeOffset.UtcNow);

        Assert.True(report.TargetMet);
        Assert.Equal(75m, report.ActualWinRate);
        Assert.All(report.Challengers, value => Assert.Equal("ShadowOnly", value.Status));
    }

    [Fact]
    public void DetectsWeakRepeatedSegmentWithoutPromotingIt()
    {
        var date = new DateOnly(2026, 9, 17);
        var trades = Enumerable.Range(0, 12).Select(index =>
            new ResearchTradeObservation(date.AddDays(-(index % 4)), "breakout", "Late", -100m, 25m));

        var report = SelfImprovementResearchAnalyzer.Analyze("nifty", date, trades, [],
            DateTimeOffset.UtcNow);

        Assert.Contains(report.Evidence, value => value.Code == "WEAK_SEGMENT");
        Assert.Contains(report.Challengers, value =>
            value.Code.StartsWith("SHADOW_", StringComparison.Ordinal));
        Assert.False(report.TargetMet);
    }
}
