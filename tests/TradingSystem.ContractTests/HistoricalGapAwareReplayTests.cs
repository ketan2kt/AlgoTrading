using Microsoft.Extensions.Logging.Abstractions;
using TradingSystem.Application.Execution;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Execution;
using TradingSystem.Infrastructure.MarketData;
using Xunit.Abstractions;

namespace TradingSystem.ContractTests;

public sealed class HistoricalGapAwareReplayTests(ITestOutputHelper output)
{
    private static readonly DateOnly Date = new(2026, 11, 11);
    private static readonly DateTimeOffset Start = new(2026, 11, 11, 9, 15, 0, TimeSpan.FromHours(5.5));

    [Fact]
    public void GapRestartsFlatAndOnlyScoresCompletedSupportedFills()
    {
        var quotes = Enumerable.Range(0, 241).Where(s => s <= 149 || s >= 160)
            .Select(s => Book(Start.AddSeconds(s), s is >= 70 and <= 74 or >= 195 and <= 199 ? 120m : 100m))
            .ToArray();
        var report = HistoricalGapAwareReplay.Run(Slice("NIFTY", quotes, Start.AddMinutes(4)),
            () => new Policy(60, 120, 180));
        Assert.Single(report.Gaps);
        Assert.Equal(2, report.Windows.Count);
        Assert.False(report.Windows[0].IndependentFlatRestart);
        Assert.Equal(1, report.Windows[0].UnresolvedExecutions);
        Assert.True(report.Windows[0].VerifiedNetPnl > 0m);
        Assert.True(report.Windows[1].IndependentFlatRestart);
        Assert.Null(report.Windows[1].VerifiedNetPnl);
        Assert.True(report.Windows[1].IndependentScenarioNetPnl > 0m);
        Assert.Equal(report.Windows[0].VerifiedNetPnl, report.VerifiedNetPnl);
        Assert.DoesNotContain(report.Windows[1].Fills, row => row.Fill.DecisionAtUtc < Start.AddSeconds(160));
    }

    [Fact]
    public void InsufficientDisplayedSizeIsNotVerifiedPnl()
    {
        var quotes = Enumerable.Range(0, 121)
            .Select(s => Book(Start.AddSeconds(s), s >= 70 ? 120m : 100m) with { OfferQuantity = 1 })
            .ToArray();
        var report = HistoricalGapAwareReplay.Run(Slice("NIFTY", quotes, Start.AddMinutes(2)), () => new Policy(60));
        Assert.Empty(report.Gaps);
        Assert.Null(report.VerifiedNetPnl);
        var fill = Assert.Single(Assert.Single(report.Windows).Fills);
        Assert.Contains("displayed size", fill.ExclusionReason);
    }

    // Opt-in diagnostic using a local, read-only copy of the actual recorder archive.
    // Does not invent policy inputs, candles, orders or a P&L verdict.
    [Theory]
    [InlineData("NIFTY", "NSE")]
    [InlineData("SENSEX", "BSE")]
    [InlineData("BANKNIFTY", "NSE")]
    public async Task November11ArchiveReportsMarketSpecificGaps(string market, string exchange)
    {
        var root = Environment.GetEnvironmentVariable("SARTHI_GAP_REPLAY_FIXTURE");
        if (string.IsNullOrEmpty(root)) return;
        var end = Start.AddMinutes(375);
        var manifest = await new FileHistoricalFeedManifestReader(Path.Combine(root, "feed"))
            .ReadEvidenceAsync(Date, Start, end, CancellationToken.None);
        Assert.NotNull(manifest);
        var universe = new ReplayRecordedContractUniverse(manifest.ContractSelectionVersion ?? "", manifest.Instruments);
        var symbols = manifest.Instruments.Where(value => value.Market == market &&
            value.Type is InstrumentType.CallOption or InstrumentType.PutOption)
            .ToDictionary(value => value.TradingSymbol, value => value.TradingSymbol, StringComparer.Ordinal);
        using var archive = new HistoricalQuoteArchive(TimeProvider.System, NullLogger<HistoricalQuoteArchive>.Instance,
            Path.Combine(root, "quotes"));
        var day = await archive.ReadReplayAsync(Date, market, exchange, symbols, Start, end,
            CancellationToken.None, streamingOnly: true, tolerateUnreadableShards: true);
        var report = HistoricalGapAwareReplay.Run(Slice(market, day.Quotes, end) with
        {
            RecordedContractUniverse = universe, UnreadableQuoteIntervals = day.UnreadableIntervals,
            CaptureSampleSeconds = manifest.SampleSeconds, ArchiveReportedCaptureGaps = manifest.HasGaps || day.HasCaptureGaps
        }, null);
        Assert.Equal(164, report.ContractsWithFreshBooks + report.ContractsWithoutFreshBooks.Count);
        Assert.True(report.ContractsWithFreshBooks >= 140);
        Assert.True(report.FreshBookSamples > 0);
        Assert.True(report.ArchiveReportedCaptureGaps);
        Assert.Null(report.VerifiedNetPnl);
        Assert.All(report.Windows, window => Assert.False(window.Simulated));
        Assert.NotEmpty(report.Gaps);
        output.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
        {
            report.Market, report.ContractsWithFreshBooks, report.ContractsWithoutFreshBooks, report.FreshBookSamples, report.Gaps,
            Windows = report.Windows.Count, ShardIntegrityGaps = day.UnreadableIntervals
        }));
        if (market == "SENSEX")
        {
            Assert.Contains(report.Gaps, gap => gap.StartUtc <= Start.AddHours(5).AddMinutes(10) &&
                gap.EndUtc >= Start.AddHours(5).AddMinutes(10));
            Assert.Contains(report.Gaps, gap => gap.StartUtc <= Start.AddHours(5).AddMinutes(17) &&
                gap.EndUtc >= Start.AddHours(5).AddMinutes(17));
        }
    }

    private static HistoricalReplayQuote Book(DateTimeOffset time, decimal mid) => new("NIFTYCE", time, mid)
    {
        Source = "GrowwFeedSample", SourceAtUtc = time, DepthSourceAtUtc = time,
        BidPrice = mid - .1m, OfferPrice = mid + .1m, BidQuantity = 100, OfferQuantity = 100
    };

    private static HistoricalReplayArchiveSlice Slice(string market, IReadOnlyList<HistoricalReplayQuote> quotes, DateTimeOffset end)
    {
        var bars = Enumerable.Range(0, (int)(end - Start).TotalMinutes)
            .Select(minute => new HistoricalReplayBar(Start.AddMinutes(minute), 60, 100, 100, 100, 100) { Symbol = market })
            .ToArray();
        return new(new(Date, market, bars, quotes, Start, end), 100m, null, [], [])
        {
            RecordedContractUniverse = new("test", [new(market, "NSE", "FNO", "1", market + "CE",
                InstrumentType.CallOption, Date.AddDays(7), 100, 1, .05m)])
        };
    }

    private sealed class Policy(params int[] seconds) : IHistoricalReplayPortfolioPolicy
    {
        public string CodeVersion => "synthetic-gap-test";
        public IReadOnlyList<HistoricalReplayCandidate> Evaluate(HistoricalReplayPortfolioFrame frame)
        {
            var at = frame.Market.AvailableAtUtc;
            return seconds.Contains((int)(at - Start).TotalSeconds)
                ? [new("NIFTYCE", at, CodeVersion, .8m, 10) { LotSize = 1 }] : [];
        }
    }
}
