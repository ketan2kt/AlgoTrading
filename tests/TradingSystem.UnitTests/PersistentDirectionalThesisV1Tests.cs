using TradingSystem.Application.Execution;
using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class PersistentDirectionalThesisV1Tests
{
    [Fact]
    public void RepeatedSoldPullbacksMaintainBearishThesisAndExposeEntryWindow()
    {
        var bars = new List<StrategyPriceBar>();
        var price = 100m;
        for (var index = 0; index < 15; index++)
        {
            var greenPullback = index is 5 or 9 or 13;
            var open = price;
            var close = greenPullback ? price + .12m : price - .22m;
            bars.Add(Bar(index, open, Math.Max(open, close) + .50m,
                Math.Min(open, close) - .50m, close));
            price = close;
        }
        // The last green candle is immediately sold, producing the current entry window.
        bars.Add(Bar(15, price, price + .20m, price - .50m, price - .18m));

        var result = PersistentDirectionalThesisAnalyzerV1.Analyze(
            bars, 100m, 101m, 99.5m);

        Assert.Equal(Direction.Sell, result.Direction);
        Assert.True(result.FailedCounterTrendAttempts >= 2);
        Assert.True(result.OpeningRangeBreakHeld);
        Assert.True(result.WouldEnter);
        Assert.Equal(DirectionalThesisPhaseV1.EntryWindow, result.Phase);
        Assert.Equal("PaperEligible", result.Status);
        Assert.Equal(new DateOnly(2026, 10, 16), result.PaperEligibleFrom);
    }

    [Fact]
    public void OrdinaryGreenCandleDoesNotErasePreviousBearishThesis()
    {
        var bars = Enumerable.Range(0, 15).Select(index =>
            Bar(index, 105m - index * .4m, 105.2m - index * .4m,
                104.3m - index * .4m, 104.6m - index * .4m)).ToList();
        bars[^1] = Bar(14, 99.0m, 99.8m, 98.9m, 99.6m);
        var previous = new PersistentDirectionalThesisV1(
            PersistentDirectionalThesisAnalyzerV1.Version, "PaperEligible",
            new DateOnly(2026, 10, 22), Direction.Sell, .75m,
            DirectionalThesisPhaseV1.Maintained, 9, 2, true, false, false, []);

        var result = PersistentDirectionalThesisAnalyzerV1.Analyze(
            bars, 102m, 105m, 103m, previous);

        Assert.Equal(Direction.Sell, result.Direction);
        Assert.False(result.ChangeOfCharacter);
        Assert.Contains(result.Evidence, value => value.Contains("retained"));
    }

    [Fact]
    public void ConfirmedChangeOfCharacterInvalidatesPriorBearishThesis()
    {
        var bars = Enumerable.Range(0, 12).Select(index =>
            Bar(index, 105m - index * .45m, 105.2m - index * .45m,
                104.2m - index * .45m, 104.55m - index * .45m)).ToList();
        bars.Add(Bar(12, 99.5m, 101m, 99.4m, 100.8m));
        bars.Add(Bar(13, 100.8m, 102m, 100.6m, 101.8m));
        bars.Add(Bar(14, 101.8m, 104m, 101.7m, 103.8m));
        var previous = new PersistentDirectionalThesisV1(
            PersistentDirectionalThesisAnalyzerV1.Version, "PaperEligible",
            new DateOnly(2026, 10, 22), Direction.Sell, .80m,
            DirectionalThesisPhaseV1.Maintained, 10, 3, true, false, false, []);

        var result = PersistentDirectionalThesisAnalyzerV1.Analyze(
            bars, 100m, 106m, 103m, previous);

        Assert.True(result.ChangeOfCharacter);
        Assert.NotEqual(Direction.Sell, result.Direction);
    }

    [Fact]
    public void ChoppyAlternationDoesNotCreateDirectionalEntry()
    {
        var bars = Enumerable.Range(0, 20).Select(index =>
        {
            var open = index % 2 == 0 ? 99.7m : 100.3m;
            var close = index % 2 == 0 ? 100.3m : 99.7m;
            return Bar(index, open, 100.5m, 99.5m, close);
        }).ToArray();

        var result = PersistentDirectionalThesisAnalyzerV1.Analyze(
            bars, 100m, 101m, 99m);

        Assert.Null(result.Direction);
        Assert.False(result.WouldEnter);
    }

    [Fact]
    public void PersistentTrendCanEnterFromCompactContinuationBaseWithoutCountertrendPattern()
    {
        var bars = new List<StrategyPriceBar>();
        var price = 110m;
        for (var index = 0; index < 12; index++)
        {
            var next = price - .25m;
            bars.Add(Bar(index, price, price + .12m, next - .12m, next));
            price = next;
        }
        for (var index = 12; index < 18; index++)
        {
            var close = price + (index % 2 == 0 ? .04m : -.02m);
            bars.Add(Bar(index, price, price + .12m, price - .12m, close));
            price = close;
        }
        bars.Add(Bar(18, price, price + .10m, price - .55m, price - .48m));

        var result = PersistentDirectionalThesisAnalyzerV1.Analyze(
            bars, 108m, 110.5m, 108.5m);

        Assert.Equal(Direction.Sell, result.Direction);
        Assert.True(result.WouldEnter);
        Assert.Contains(result.Evidence, value => value.Contains("continuation base"));
    }

    [Fact]
    public void CompletedPersistentPullbackCreatesBoundedPaperOnlyEntry()
    {
        var bars = Enumerable.Range(0, 15).Select(index =>
        {
            var open = 101m - index * .05m;
            var close = open - .03m;
            return Bar(index, open, open + .10m, close - .10m, close);
        }).ToArray();
        var thesis = new PersistentDirectionalThesisV1(
            PersistentDirectionalThesisAnalyzerV1.Version,
            PersistentDirectionalThesisAnalyzerV1.Status,
            PersistentDirectionalThesisAnalyzerV1.PaperEligibleFrom,
            Direction.Sell, .71m, DirectionalThesisPhaseV1.EntryWindow,
            10, 2, false, false, true, ["Persistent bearish thesis."]);

        var result = PersistentDirectionalPaperEntryPolicyV1.Evaluate(bars, thesis);

        Assert.Equal(Direction.Sell, result.Direction);
        Assert.Equal(.71m, result.Confidence);
        Assert.True(result.StructuralStop > bars[^1].Close);
        Assert.True(result.Target < bars[^1].Close);
        Assert.StartsWith("PaperOnly|", PersistentDirectionalPaperEntryPolicyV1.StrategyCode("nifty"));
    }

    [Fact]
    public void ThesisWithoutCompletedWindowCannotCreatePaperEntry()
    {
        var bars = Enumerable.Range(0, 15).Select(index =>
            Bar(index, 100m, 100.4m, 99.6m, 99.9m)).ToArray();
        var thesis = new PersistentDirectionalThesisV1(
            PersistentDirectionalThesisAnalyzerV1.Version,
            PersistentDirectionalThesisAnalyzerV1.Status,
            PersistentDirectionalThesisAnalyzerV1.PaperEligibleFrom,
            Direction.Sell, .70m, DirectionalThesisPhaseV1.Maintained,
            9, 1, false, false, false, []);

        Assert.Null(PersistentDirectionalPaperEntryPolicyV1.Evaluate(bars, thesis).Direction);
    }

    [Fact]
    public void BoundsWideStructuralRiskInsteadOfRejectingPaperEntry()
    {
        var bars = Enumerable.Range(0, 15).Select(index =>
            Bar(index, 100m, 101m, 99m, 100m)).ToArray();
        bars[^5] = bars[^5] with { High = 120m };
        var thesis = new PersistentDirectionalThesisV1(
            PersistentDirectionalThesisAnalyzerV1.Version,
            PersistentDirectionalThesisAnalyzerV1.Status,
            PersistentDirectionalThesisAnalyzerV1.PaperEligibleFrom,
            Direction.Sell, .70m, DirectionalThesisPhaseV1.EntryWindow,
            8, 1, true, false, true, ["test"]);

        var result = PersistentDirectionalPaperEntryPolicyV1.Evaluate(bars, thesis);

        Assert.Equal(Direction.Sell, result.Direction);
        Assert.Contains(result.Reasons, value => value.Contains("bounded"));
    }

    private static StrategyPriceBar Bar(int minute, decimal open, decimal high,
        decimal low, decimal close) => new(
        new DateTimeOffset(2026, 10, 8, 3, 45, 0, TimeSpan.Zero).AddMinutes(minute),
        open, high, low, close);
}
