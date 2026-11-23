using System.Text.Json;
using TradingSystem.Application.Execution;
using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class NiftyMorningEntryQualityPolicyTests
{
    private static (DateTimeOffset At, Direction Side, bool Allowed, string Reason,
        StrategyPriceBar[] Bars)[] Cases()
    {
        using var stream = typeof(NiftyMorningEntryQualityPolicyTests).Assembly.GetManifestResourceStream(
            "TradingSystem.UnitTests.RegressionFixtures.nifty-morning-entry-202611.json")!;
        using var doc = JsonDocument.Parse(stream);
        return doc.RootElement.EnumerateArray().Select(r => (
            r.GetProperty("decision").GetDateTimeOffset(),
            Enum.Parse<Direction>(r.GetProperty("direction").GetString()!),
            r.GetProperty("expected").GetBoolean(), r.GetProperty("reason").GetString()!,
            r.GetProperty("bars").EnumerateArray().Select(b => new StrategyPriceBar(
                b.GetProperty("at").GetDateTimeOffset(), b.GetProperty("o").GetDecimal(),
                b.GetProperty("h").GetDecimal(), b.GetProperty("l").GetDecimal(),
                b.GetProperty("c").GetDecimal())).ToArray())).ToArray();
    }

    [Fact]
    public void RecordedLosingEntriesDeferredAndSubsequentWinnerRetained()
    {
        foreach (var row in Cases())
        {
            Assert.All(row.Bars, b => Assert.True(b.OpenTimeUtc.AddMinutes(1) <= row.At));
            var result = NiftyMorningEntryQualityPolicy.Evaluate(
                TimeOnly.FromDateTime(row.At.DateTime), row.Bars, row.Side);
            Assert.Equal(row.Allowed, result.Permitted);
            Assert.Equal(row.Reason, result.RejectionOutcome);
            var mirrored = row.Bars.Select(b => new StrategyPriceBar(b.OpenTimeUtc,
                60000m-b.Open, 60000m-b.Low, 60000m-b.High, 60000m-b.Close)).ToArray();
            Assert.Equal(row.Allowed, NiftyMorningEntryQualityPolicy.Evaluate(
                TimeOnly.FromDateTime(row.At.DateTime), mirrored,
                row.Side == Direction.Buy ? Direction.Sell : Direction.Buy).Permitted);
        }
    }

    [Theory]
    [InlineData(9, 29, 59, true)]
    [InlineData(9, 30, 0, false)]
    [InlineData(10, 59, 59, false)]
    [InlineData(11, 0, 0, true)]
    [InlineData(13, 0, 0, true)]
    [InlineData(14, 59, 0, true)]
    public void ExactIstWindowLeavesEarlierAndLaterEntriesUnchanged(int hour, int minute, int second, bool allowed)
    {
        var row = Cases().First(r => r.Reason == "NiftyMorningImpulseRejected");
        Assert.Equal(allowed, NiftyMorningEntryQualityPolicy.Evaluate(
            new TimeOnly(hour, minute, second), row.Bars, row.Side).Permitted);
    }

    [Fact]
    public void LowFlipTrendDoesNotAcquireAnExtraConfirmationRequirement()
    {
        var at = new DateTimeOffset(2026, 11, 17, 9, 15, 0, TimeSpan.FromHours(5.5));
        var bars = Enumerable.Range(0, 20).Select(i => new StrategyPriceBar(
            at.AddMinutes(i), 100+i, 101+i, 99+i, 100.8m+i)).ToArray();
        // Even an opposing candidate remains for the existing countertrend/risk gates.
        Assert.True(NiftyMorningEntryQualityPolicy.Evaluate(new(9, 35), bars, Direction.Sell).Permitted);
    }

    [Fact]
    public void MissingBarsAndWarmupAreLeftToExistingDataGates()
    {
        var row = Cases().First();
        Assert.True(NiftyMorningEntryQualityPolicy.Evaluate(new(10, 46),
            row.Bars.TakeLast(14).ToArray(), row.Side).Permitted);
        Assert.True(NiftyMorningEntryQualityPolicy.Evaluate(new(10, 46),
            row.Bars.Where((_, i) => i != row.Bars.Length-2).ToArray(), row.Side).Permitted);
        Assert.True(NiftyMorningEntryQualityPolicy.Evaluate(new(10, 46),
            row.Bars, row.Side, 300).Permitted);
    }

    [Fact]
    public void SharedGateRequiresExplicitNiftyOptIn()
    {
        foreach (var row in Cases().Where(r => !r.Allowed))
        {
            var frames = MultiTimeframeDecisionPolicy.Analyze(row.Bars, row.Side,
                row.Bars.Max(b=>b.High), row.Bars.Min(b=>b.Low));
            NiftyPreQuoteEntryGateDecision Evaluate(bool nifty) => NiftyPreQuoteEntryGatePolicy.Evaluate(
                TimeOnly.FromDateTime(row.At.DateTime), row.Bars, [], row.Side,
                MarketStructureQualitySnapshot.Unavailable, frames, 60, 10,
                row.Bars.Max(b=>b.High), row.Bars.Min(b=>b.Low),
                applyNiftyMorningQuality: nifty);
            Assert.Equal(row.Reason, Evaluate(true).RejectionOutcome);
            Assert.DoesNotContain("NiftyMorning", Evaluate(false).RejectionOutcome);
        }
    }
}
