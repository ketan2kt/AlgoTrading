using TradingSystem.Application.Execution;
using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class SensexEntrySelectionPolicyTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 4,
        3, 45, 0, TimeSpan.Zero);
    private static readonly StrategyPriceBar[] Bars = Enumerable.Range(0, 15)
        .Select(index => new StrategyPriceBar(Start.AddMinutes(index * 5),
            100m, 102m, 98m, 100m)).ToArray();
    private static readonly MarketStateAssessmentV2 Neutral = new(
        IntradayMarketStateV2.Unavailable, null, 0m, 0m, 0m, 0m, 0m,
        0m, 0m, []);
    private static readonly SensexSetupLifecycle EntryWindow = new(
        SensexSetupPhase.EntryWindow, Direction.Buy, 3, .2m, .3m, []);

    [Fact]
    public void ExistingMomentumCandidateKeepsItsDirectionWithoutSupplement()
    {
        var primary = new SensexEntryCandidate(Direction.Buy, .75m,
            "Index momentum breakout", ["Breakout"]);

        var selected = SensexEntrySelectionPolicy.Evaluate(primary, Bars,
            EntryWindow, null, Neutral, 100m, 100m, 100m, false);

        Assert.Equal(primary, selected.Candidate);
        Assert.False(selected.Supplemental);
        Assert.Null(selected.EarlyCandidate);
    }

    [Fact]
    public void CompletedLifecycleCanSupplyEarlyCandidateWhenMomentumHasNone()
    {
        var selected = SensexEntrySelectionPolicy.Evaluate(
            new(null, .5m, "Index momentum breakout", []), Bars,
            EntryWindow, null, Neutral, 100m, 100m, 100m, false);

        Assert.Equal(Direction.Buy, selected.Candidate.Direction);
        Assert.Equal("Sensex controlled early pullback",
            selected.Candidate.Strategy);
        Assert.True(selected.Supplemental);
        Assert.False(selected.PersistentDirectional);
        Assert.True(selected.EarlyAssessment?.Permitted);
    }

    [Fact]
    public void ActiveSensexPositionBlocksSupplementalCandidate()
    {
        var selected = SensexEntrySelectionPolicy.Evaluate(
            new(null, .5m, "Index momentum breakout", []), Bars,
            EntryWindow, null, Neutral, 100m, 100m, 100m, true);

        Assert.Null(selected.Candidate.Direction);
        Assert.False(selected.Supplemental);
        Assert.False(selected.EarlyAssessment?.Permitted);
    }
}
