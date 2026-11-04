using TradingSystem.Application.Execution;

namespace TradingSystem.UnitTests;

public sealed class HistoricalArchivePathCanonicalizerTests
{
    private static readonly DateTimeOffset Minute = new(2026, 10, 29, 4, 0,
        0, TimeSpan.Zero);

    [Fact]
    public void IdenticalMappedAndResearchMinutesCollapse()
    {
        var bar = new HistoricalReplayBar(Minute, 60, 100m, 101m, 99m, 100m)
        { Symbol = "NIFTY22000CE", Volume = 42 };
        var quote = new HistoricalReplayQuote(bar.Symbol, Minute.AddMinutes(1), 100m)
        { Volume = 42 };
        Assert.Single(HistoricalArchivePathCanonicalizer.Bars([bar, bar with { }]));
        Assert.Single(HistoricalArchivePathCanonicalizer.Quotes([
            quote, quote with { }
        ]));
    }

    [Fact]
    public void ResearchRepairsFillOnlyMissingMinutesAndNeverRewriteRecordedPrices()
    {
        var recorded = new HistoricalReplayBar(Minute, 60, 100m, 101m, 99m, 100m) { Symbol = "NIFTY" };
        var repaired = recorded with { OpenTimeUtc = Minute.AddMinutes(1), Source = "OfficialHistoricalRepair" };
        var result = HistoricalArchivePathCanonicalizer.PreferRecordedBars([recorded],
            [recorded with { Close = 100.5m, Source = "OfficialHistoricalRepair" }, repaired]);
        Assert.Equal(new[] { recorded, repaired }, result);
        Assert.Throws<InvalidOperationException>(() => HistoricalArchivePathCanonicalizer.PreferRecordedBars([recorded],
            [repaired, repaired with { Close = 100.5m }]));
    }

    [Fact]
    public void ConflictingSourceRevisionsFailClosed()
    {
        var bar = new HistoricalReplayBar(Minute, 60, 100m, 101m, 99m, 100m)
        { Symbol = "NIFTY22000CE" };
        var quote = new HistoricalReplayQuote(bar.Symbol, Minute.AddMinutes(1), 100m);
        Assert.Throws<InvalidOperationException>(() =>
            HistoricalArchivePathCanonicalizer.Bars([
                bar, bar with { Close = 100.5m }
            ]));
        Assert.Throws<InvalidOperationException>(() =>
            HistoricalArchivePathCanonicalizer.Quotes([
                quote, quote with { LastPrice = 100.5m }
            ]));
        Assert.Throws<InvalidOperationException>(() =>
            HistoricalArchivePathCanonicalizer.Quotes([
                quote, quote with { Source = "GrowwChainReceiptLtp" }
            ]));
    }
}
