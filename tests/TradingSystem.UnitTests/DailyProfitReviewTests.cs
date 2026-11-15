using TradingSystem.Application.Diagnostics;

namespace TradingSystem.UnitTests;

public sealed class DailyProfitReviewTests
{
    private static readonly DateTimeOffset Open = new(2026, 11, 13, 4, 0, 0, TimeSpan.Zero);
    private static DailyTradingTrade Trade(string market = "NIFTY", decimal gross = 400, decimal costs = 50) =>
        new(market, "MarketPaperPosition", Guid.NewGuid(), null, "TESTPE", "Core", "Buy", 100,
            100, 104, Open, Open.AddMinutes(10), "Closed", "FailedSetupExit", gross, costs, gross - costs,
            1000, Open.AddMinutes(5), 100, Open.AddMinutes(2), "Persisted quote observations; excursions are gross before fees", 20);
    private static DailyProfitReview Build(DailyTradingTrade trade, int? finalUnits = 100,
        IReadOnlyList<DailyFollowUpPrice>? followUps = null, bool incomparable = false) =>
        DailyProfitReviewBuilder.Build([trade], new Dictionary<Guid, DailyEntryReceipt>(),
            new Dictionary<Guid, IReadOnlyList<DailyFollowUpPrice>> { [trade.TradeId] = followUps ?? [] },
            finalUnits is { } units ? new Dictionary<Guid, int> { [trade.TradeId] = units } : new Dictionary<Guid, int>(),
            incomparable ? new HashSet<Guid> { trade.TradeId } : new HashSet<Guid>());

    [Theory]
    [InlineData("NIFTY", "NIFTY")]
    [InlineData("sensex", "SENSEX")]
    [InlineData("BANK-NIFTY", "BANKNIFTY")]
    public void ReconcilesAllThreeWithoutSubtractingFeesTwice(string market, string normalized)
    {
        var report = Build(Trade(market)); var row = Assert.Single(report.Trades);
        Assert.Equal(600m, row.GrossGiveback); Assert.Equal(40m, row.RetainedPercent);
        Assert.Equal(3, report.Markets.Count);
        var summary = report.Markets.Single(m => m.Market == normalized);
        Assert.Equal(350m, summary.NetPnl); Assert.Equal(50m, summary.Costs);
        Assert.Equal(600m, summary.GrossGivebackSubtotal); Assert.Equal(1, summary.ComparablePeakTrades);
        Assert.All(report.Markets.Where(m => m.Market != normalized), m => Assert.Null(m.ObservedPeakSubtotal));
    }

    [Fact]
    public void PositivePeakToLossUsesNetAndDoesNotClampNegativeRetention()
    {
        var report = Build(Trade(gross: -200));
        Assert.Equal(1200m, report.Trades[0].GrossGiveback);
        Assert.Equal(-20m, report.Trades[0].RetainedPercent);
        Assert.Equal(1, report.Markets[0].PositivePeakToNetLossTrades);
        Assert.Equal(1, Build(Trade(gross: 20, costs: 50)).Markets[0].PositivePeakToNetLossTrades);
    }

    [Theory]
    [InlineData("legacy excursion accounting may mix gross and net")]
    [InlineData("earlier path unavailable")]
    [InlineData("historical intratrade path unavailable")]
    public void UnknownPeakBasisNeverCountsAsZero(string coverage)
    {
        var report = Build(Trade() with { ExcursionCoverage = coverage });
        Assert.Null(report.Trades[0].GrossGiveback); Assert.Null(report.Markets[0].GrossGivebackSubtotal);
        Assert.Equal(350m, report.Markets[0].NetPnl);
    }

    [Fact]
    public void RejectsSizeChangedPathsOpenTradesAndPeakBelowRealisedGross()
    {
        Assert.Null(Build(Trade(), incomparable: true).Trades[0].GrossGiveback);
        var open = Build(Trade() with { ClosedAtUtc = null });
        Assert.Null(open.Trades[0].GrossGiveback); Assert.Equal(1, open.Markets[0].OpenTrades);
        Assert.Equal(0m, open.Markets[0].NetPnl);
        Assert.Null(Build(Trade(gross: 1200)).Trades[0].GrossGiveback);
        Assert.Null(Build(Trade() with { ExcursionSampleCount = 2 }).Trades[0].GrossGiveback);
    }

    [Fact]
    public void FollowUpUsesOnlyRemainingUnitsAndShowsBothDirectionsNotBestPeak()
    {
        var trade = Trade(); var report = Build(trade, 60,
            [new(5, trade.ClosedAtUtc!.Value.AddMinutes(5), 110), new(15, trade.ClosedAtUtc.Value.AddMinutes(15), 100)]);
        var row = report.Trades[0];
        Assert.Equal("Mixed follow-up", row.ExitAssessment);
        Assert.Equal(360m, row.FollowUps[0].GrossDifference); Assert.Equal(-240m, row.FollowUps[1].GrossDifference);
        Assert.Equal(60, row.FinalExitUnits);
    }

    [Fact]
    public void NiftyPutSignalIsStillALongOptionAndMissingSizeIsNotAssumed()
    {
        var trade = Trade() with { Source = "PaperTradeResult", Direction = "Sell" };
        DailyFollowUpPrice[] samples = [new(5, trade.ClosedAtUtc!.Value.AddMinutes(5), 110)];
        Assert.Equal(600m, Build(trade, followUps: samples).Trades[0].FollowUps[0].GrossDifference);
        Assert.Empty(Build(trade, null, samples).Trades[0].FollowUps);
    }

    [Fact]
    public void ExcludesEarlyLateAfterHoursInvalidAndDuplicateFollowUps()
    {
        var trade = Trade(); var close = trade.ClosedAtUtc!.Value;
        var row = Build(trade, followUps: [new(5, close.AddMinutes(4), 200), new(5, close.AddMinutes(5), 110),
            new(5, close.AddMinutes(6), 120), new(15, close.AddMinutes(26), 500), new(30, close.AddMinutes(30), 0),
            new(60, close.AddDays(1), 1000)]).Trades[0];
        Assert.Single(row.FollowUps); Assert.Equal(110m, row.FollowUps[0].Price);
        var late = trade with { ClosedAtUtc = new DateTimeOffset(2026, 11, 13, 9, 58, 0, TimeSpan.Zero) };
        Assert.Empty(Build(late, followUps: [new(5, late.ClosedAtUtc.Value.AddMinutes(5), 200)]).Trades[0].FollowUps);
    }

    [Fact]
    public void EntryUsesLinkedReceiptNotOutcomeAndAdvisoryIsNotAConfirmedLateEntry()
    {
        var entry = DailyProfitReviewBuilder.Entry(new(Open,
            """{"timing":{"extensionAtr":1.2,"roomAtr":0.3,"candleCloseAgeSeconds":4,"extendedHypothesis":true},"reasons":["Breakout accepted"],"entryQuality":{"Reasons":["Depth present"]}}"""));
        Assert.Equal("Timing flag (advisory)", entry.Status); Assert.Equal(1.2m, entry.ExtensionAtr);
        Assert.Contains("Depth present", entry.Reasons); Assert.Contains(entry.Reasons, r => r.Contains("First actionable"));
        Assert.Equal("Timing unverified", DailyProfitReviewBuilder.Entry(null).Status);
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"observedPrice\":\"bad\",\"positionId\":12}")]
    public void MalformedLegacyEvidenceDoesNotBreakReport(string json)
    {
        Assert.Null(DailyProfitReviewBuilder.PositionId(json)); Assert.Null(DailyProfitReviewBuilder.FollowUp(json));
        Assert.NotNull(DailyProfitReviewBuilder.Entry(new(Open, json)));
    }
}
