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

    [Theory]
    [InlineData(Direction.Buy)]
    [InlineData(Direction.Sell)]
    public void PersistentThesisCannotOpenAtExcessiveExtensionOrBypassThroughPrimary(Direction side)
    {
        var bars = Enumerable.Range(0, 30).Select(i => new StrategyPriceBar(Start.AddMinutes(i),
            100m, 101m, 99m, 100m)).ToArray();
        bars[^2] = bars[^2] with { Close = 99.5m, High = 100.2m };
        bars[^1] = bars[^1] with { Open = 99.5m, Close = 105m, High = 105.2m };
        if (side == Direction.Sell) bars = bars.Select(b => b with
            { Open = 200m-b.Open, High = 200m-b.Low, Low = 200m-b.High, Close = 200m-b.Close }).ToArray();
        var thesis = new PersistentDirectionalThesisV1(PersistentDirectionalThesisAnalyzerV1.Version,
            PersistentDirectionalThesisAnalyzerV1.Status, new(2026, 10, 16), side, .8m,
            DirectionalThesisPhaseV1.EntryWindow, 10, 2, true, false, true, []);
        Assert.True(PersistentDirectionalPaperEntryPolicyV1.Evaluate(bars, thesis).Direction is not null);
        Assert.True(PersistentDirectionalThesisAnalyzerV1.EntryExtensionAtr(bars) > 1.5m);
        var rejected = Select();
        Assert.Null(rejected.Candidate.Direction);
        Assert.True(rejected.PersistentDirectional);
        Assert.Contains(rejected.Candidate.Reasons, r => r.Contains("limit 1.50"));

        bars[^1] = bars[^1] with { Close = side == Direction.Buy ? 102m : 98m };
        Assert.True(PersistentDirectionalThesisAnalyzerV1.EntryExtensionAtr(bars) <= 1.5m);
        Assert.Equal(side, Select().Candidate.Direction);

        SensexEntrySelection Select() => SensexEntrySelectionPolicy.Evaluate(
            new(side, .8m, "Index momentum breakout", []), bars, EntryWindow, thesis, Neutral, 100m, 100m, 100m, false);
    }

    [Fact]
    public void SavedSensexReceiptClassificationKeepsTheThreeWinningPersistentEntries()
    {
        Assert.Equal(1.5m, SensexEntrySelectionPolicy.MaximumPersistentEntryExtensionAtr);
        // Saved 17Nov persistent-entry extension receipts: only 12:03 was beyond
        // the budget. This classification is not an execution/P&L replay.
        decimal[] retainedExtensions = [.54m, .93m, 1.02m, 1.43m];
        Assert.All(retainedExtensions, extension =>
            Assert.True(extension <= SensexEntrySelectionPolicy.MaximumPersistentEntryExtensionAtr));
        Assert.True(1.78m > SensexEntrySelectionPolicy.MaximumPersistentEntryExtensionAtr);
    }
}
