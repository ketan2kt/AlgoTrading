using TradingSystem.Application.Execution;
using TradingSystem.Application.Strategies;

namespace TradingSystem.UnitTests;

public sealed class IntradayExpansionPairPolicyTests
{
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
