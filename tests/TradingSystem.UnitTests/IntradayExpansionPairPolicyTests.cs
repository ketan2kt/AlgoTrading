using TradingSystem.Application.Execution;
using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class IntradayExpansionPairPolicyTests
{
    [Fact]
    public void OneRupeeLegExitKeepsRealisedProceedsAndOtherLegOpen()
    {
        var now = new DateTimeOffset(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);
        var call = new MarketPaperPosition(Guid.NewGuid(), "NIFTY", Guid.NewGuid(),
            Guid.NewGuid(), "Expansion Pair|test|CE", Direction.Buy, 100,
            50m, ExpansionPairExitPolicy.LegFloorStop, 500m, now);
        var put = new MarketPaperPosition(Guid.NewGuid(), "NIFTY", Guid.NewGuid(),
            Guid.NewGuid(), "Expansion Pair|test|PE", Direction.Buy, 100,
            50m, ExpansionPairExitPolicy.LegFloorStop, 500m, now);

        call.Mark(1m, call.StopLoss, now.AddMinutes(1));
        Assert.True(ExpansionPairExitPolicy.ShouldStopLeg(call));
        call.Close(1m, 5m, "ExpansionPairLegStop", now.AddMinutes(1));
        Assert.Equal(5_100m, ExpansionPairExitPolicy.MarkedBasketValue([call, put]));
        Assert.Equal(ExpansionPairExitAction.StopBasket,
            ExpansionPairExitPolicy.Evaluate(10_000m, 5_100m, 10_000m,
                TimeSpan.FromMinutes(1), new TimeOnly(11, 0), false,
                .28m, .10m, .25m, new TimeOnly(15, 10)));
        put.Mark(140m, put.StopLoss, now.AddMinutes(1));

        Assert.False(ExpansionPairExitPolicy.ShouldStopLeg(call));
        Assert.False(ExpansionPairExitPolicy.ShouldStopLeg(put));
        Assert.Equal(14_100m, ExpansionPairExitPolicy.MarkedBasketValue([call, put]));
        Assert.Equal("Active", put.Status);
    }

    [Fact]
    public void AggregationExcludesIncompleteAndMissingMinuteBuckets()
    {
        var start = new DateTimeOffset(2026, 10, 23, 4, 0, 0, TimeSpan.Zero);
        var minutes = Enumerable.Range(0, 12).Where(i => i != 7)
            .Select(i => Bar(start.AddMinutes(i), 100m + i, 2m, .5m)).ToArray();
        var result = IntradayExpansionPairPolicy.CompletedFiveMinuteBars(minutes, 60, start.AddMinutes(12));
        Assert.Single(result);
        Assert.Equal(100m, result[0].Open);
        Assert.Equal(104.5m, result[0].Close);
        Assert.Equal(105m, result[0].High);
    }

    [Fact]
    public void ElevenMinuteCandlesCannotMasqueradeAsElevenFiveMinuteCandles()
    {
        var start = new DateTimeOffset(2026, 10, 23, 4, 0, 0, TimeSpan.Zero);
        var minutes = Enumerable.Range(0, 11)
            .Select(i => Bar(start.AddMinutes(i), 100m, 2m, .5m)).ToArray();
        var result = IntradayExpansionPairPolicy.CompletedFiveMinuteBars(minutes, 60, start.AddMinutes(11));
        Assert.Equal(2, result.Length);
        Assert.False(IntradayExpansionPairPolicy.Evaluate(result).Permitted);
    }

    [Fact]
    [Trait("Category", "DeploymentRegression")]
    public void EntryRequiresRealCompressionReleaseBeforeDirectionIsMature()
    {
        var start = new DateTimeOffset(2026, 10, 21, 4, 45, 0, TimeSpan.Zero);
        var bars = new List<StrategyPriceBar>();
        for (var index = 0; index < 6; index++)
            bars.Add(Bar(start.AddMinutes(index * 5), 23000m + index, 20m, 2m));
        bars.Add(Bar(start.AddMinutes(30), 23005m, 10m, 1m));
        bars.Add(Bar(start.AddMinutes(35), 23006m, 9m, -1m));
        bars.Add(Bar(start.AddMinutes(40), 23005m, 10m, 1m));
        bars.Add(Bar(start.AddMinutes(45), 23006m, 9m, -1m));
        bars.Add(new(start.AddMinutes(50), 23005m, 23018m, 23004m, 23015m));

        var result = IntradayExpansionPairPolicy.Evaluate(bars);

        Assert.True(result.Permitted);
        Assert.True(result.Confidence >= .55m);
    }

    [Fact]
    [Trait("Category", "DeploymentRegression")]
    public void EntryRejectsAnOrdinaryNonCompressedMarket()
    {
        var start = DateTimeOffset.UtcNow;
        var bars = Enumerable.Range(0, 11)
            .Select(index => Bar(start.AddMinutes(index * 5), 23000m + index, 20m, 3m)).ToArray();

        var result = IntradayExpansionPairPolicy.Evaluate(bars);

        Assert.False(result.Permitted);
        Assert.Contains(result.Reasons, value => value.Contains("squeeze", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    [Trait("Category", "DeploymentRegression")]
    public void CandidateSelectionBalancesDebitAndCapsDefinedRisk()
    {
        var expiry = new DateOnly(2026, 10, 27);
        var result = ExpansionPairCandidatePolicy.Evaluate(Candidate("CE", 62m, expiry),
            Candidate("PE", 55m, expiry), 170m, 1.45m, 5000m, .28m, 2);

        Assert.True(result.Permitted);
        Assert.InRange(result.Lots, 1, 2);
        Assert.True(result.EstimatedRisk <= 5000m);
    }

    [Fact]
    [Trait("Category", "DeploymentRegression")]
    public void CandidateSelectionRejectsUnbalancedSensitivity()
    {
        var expiry = new DateOnly(2026, 10, 27);
        var result = ExpansionPairCandidatePolicy.Evaluate(Candidate("CE", 90m, expiry),
            Candidate("PE", 35m, expiry), 170m, 1.45m, 5000m, .28m, 2);

        Assert.False(result.Permitted);
        Assert.Contains(result.Reasons, value => value.Contains("ratio", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [Trait("Category", "DeploymentRegression")]
    [InlineData(70, 100, 110, 5, 11, 0, ExpansionPairExitAction.StopBasket)]
    [InlineData(105, 100, 105, 16, 11, 0, ExpansionPairExitAction.TimeExit)]
    [InlineData(126, 100, 126, 5, 11, 0, ExpansionPairExitAction.RecoverAndRun)]
    [InlineData(110, 100, 150, 20, 11, 1, ExpansionPairExitAction.TrailRunner)]
    [InlineData(109, 100, 120, 10, 11, 0, ExpansionPairExitAction.ProtectBasketProfit)]
    [InlineData(115, 100, 120, 10, 11, 0, ExpansionPairExitAction.Hold)]
    public void ExitPolicyControlsCombinedDebitAndPreservesRunner(decimal current,
        decimal entry, decimal peak, int ageMinutes, int hour, int recovery,
        ExpansionPairExitAction expected)
    {
        var result = ExpansionPairExitPolicy.Evaluate(entry, current, peak,
            TimeSpan.FromMinutes(ageMinutes), new TimeOnly(hour, 0), recovery == 1,
            .28m, .10m, .25m, new TimeOnly(15, 10));

        Assert.Equal(expected, result);
    }

    private static StrategyPriceBar Bar(DateTimeOffset time, decimal midpoint, decimal range,
        decimal change) => new(time, midpoint, midpoint + range / 2m,
            midpoint - range / 2m, midpoint + change);

    private static HeroZeroCandidate Candidate(string type, decimal premium, DateOnly expiry) =>
        new(Guid.NewGuid(), $"TEST{type}", type, 23000m, expiry, 65, premium,
            premium - 1m, premium, 1.6m, 10000m, 20000m, 500m, .75m);
}
