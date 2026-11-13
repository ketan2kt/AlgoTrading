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
        var window = Assert.Single(report.Windows);
        Assert.Empty(window.Fills);
        Assert.Equal(1, window.UnresolvedExecutions);
        Assert.Collection(window.EntryEvidence,
            row => Assert.Equal("Candidate", row.Stage),
            row => Assert.Equal("Queued", row.Stage),
            row => Assert.Equal("Entry unresolved", row.Stage));
    }

    [Fact]
    public void ExpiredExitDoesNotKeepGhostPositionOrCreditTheFlatRestart()
    {
        var quotes = Enumerable.Range(0, 361).Select(second =>
            Book(Start.AddSeconds(second), second is >= 65 and < 180 ? 80m : second is >= 250 and < 255 ? 120m : 100m)
                with { BidQuantity = second is >= 65 and <= 155 ? 1 : 100 }).ToArray();
        var stages = new List<HistoricalReplayTimingSample>();
        var report = HistoricalGapAwareReplay.Run(Slice("NIFTY", quotes, Start.AddMinutes(6)),
            () => new Policy(60, 240), timing: new(stages.Add));

        Assert.Empty(report.Gaps);
        Assert.Equal(2, report.Windows.Count);
        var opening = report.Windows[0];
        Assert.True(opening.StoppedOnExecutionUncertainty);
        Assert.Equal(Start.AddSeconds(156), opening.EndUtc);
        Assert.Equal(Start.AddSeconds(61), opening.ExecutionUncertainSinceUtc);
        Assert.Equal(1, opening.UnresolvedExecutions);
        Assert.Contains(opening.EntryEvidence, item => item.Stage == "Exit unsupported" && item.AtUtc == opening.EndUtc);
        Assert.Empty(opening.Fills);
        Assert.Null(report.VerifiedNetPnl);
        var later = report.Windows[1];
        Assert.True(later.IndependentFlatRestart);
        Assert.True(later.IndependentScenarioNetPnl > 0m);
        Assert.Null(later.VerifiedNetPnl);
        Assert.Contains(stages, step => step.Stage == "execution-uncertain");
        Assert.Contains(stages, step => step.Stage == "market-complete" && step.Totals.Contains("quote-execution", StringComparison.Ordinal));
        var paper = new RecordedPaperSession([
            new("NIFTYCE", [new("Entry", Start.AddSeconds(62), 10, 100), new("Exit", Start.AddSeconds(80), 10, 80)]),
            new("NIFTYCE", [new("Entry", Start.AddSeconds(125), 10, 100), new("Exit", Start.AddSeconds(145), 10, 80)])
                { DecisionBarCloseUtc = Start.AddSeconds(120) }
        ], ["EngineEvidence.sensex.regular: unrelated engine gap."]);
        var comparison = HistoricalGapAwareReplayService.CompareOpeningWindows([report], paper);
        Assert.NotNull(comparison);
        Assert.Empty(comparison.Gaps);
        var row = Assert.Single(comparison.Rows);
        Assert.Equal("Exit unsupported", row.EntryEvidence?.Stage);
        Assert.Null(row.Replay);
    }

    [Fact]
    public void SplitExitDiagnosticNeverReusesTimestampOrUnchangedSizeAtSamePrice()
    {
        var quotes = Enumerable.Range(0, 181).Select(second =>
        {
            var at = Start.AddSeconds(second);
            if (second < 65) return Book(at, 100m);
            if (second == 69) return Book(at, 79m) with { BidQuantity = 5 };
            if (second is 67 or 68) return Book(at, 80m) with
            { BidQuantity = 7, DepthSourceAtUtc = second == 67 ? at.AddSeconds(-1) : at };
            return Book(at, 80m) with { BidQuantity = 3 };
        }).ToArray();
        var report = HistoricalGapAwareReplay.Run(Slice("NIFTY", quotes, Start.AddMinutes(3)), () => new Policy(60));
        var opening = report.Windows[0];
        var scenario = Assert.Single(opening.ExitLiquidityScenarios);
        Assert.Equal(10, scenario.ModeledUnits);
        Assert.Equal(3, scenario.Fills.Count);
        Assert.Equal([Start.AddSeconds(66), Start.AddSeconds(68), Start.AddSeconds(69)],
            scenario.Fills.Select(fill => fill.AtUtc));
        Assert.Equal([3, 4, 3], scenario.Fills.Select(fill => fill.Units));
        Assert.NotNull(scenario.ModeledNetPnl);
        Assert.Null(report.VerifiedNetPnl);
        Assert.Empty(opening.Fills);
    }

    [Fact]
    public void RecordedPersistenceGapSeparatesReplayEvenWhenAvailableQuotesLookContinuous()
    {
        var quotes = Enumerable.Range(0, 241).Select(s => Book(Start.AddSeconds(s), 100m)).ToArray();
        var gap = new HistoricalReplayGap(Start.AddSeconds(145), Start.AddSeconds(180), "Recorder depth gap");
        var slice = Slice("NIFTY", quotes, Start.AddMinutes(4)) with { MonitoredDepthGaps = [gap] };
        var report = HistoricalGapAwareReplay.Run(slice, () => new Policy(120));
        Assert.Equal(gap, Assert.Single(report.Gaps));
        Assert.Equal(2, report.Windows.Count);
        Assert.True(report.Windows[1].IndependentFlatRestart);
        Assert.Null(report.Windows[1].VerifiedNetPnl);
    }

    [Fact]
    public void FragmentedSessionStillSimulatesActionableWindowsAfterTheThirtySecondWindow()
    {
        const int decision = 38 * 60;
        var end = Start.AddMinutes(42);
        var quotes = Enumerable.Range(0, 42 * 60 + 1).Select(second =>
            Book(Start.AddSeconds(second), second is >= decision + 8 and <= decision + 12 ? 120m : 100m)).ToArray();
        var slice = Slice("NIFTY", quotes, end) with
        {
            MonitoredDepthGaps = Enumerable.Range(0, 41).Select(minute =>
                new HistoricalReplayGap(Start.AddMinutes(minute).AddSeconds(45), Start.AddMinutes(minute).AddSeconds(55),
                    "Recorder depth gap")).ToArray()
        };
        var policiesCreated = 0;
        var report = HistoricalGapAwareReplay.Run(slice, () => { policiesCreated++; return new Policy(decision); });

        Assert.Equal(42, report.Windows.Count);
        Assert.Equal(41, report.Gaps.Count);
        Assert.Equal(41, policiesCreated); // The opening fragment contains no completed decision bar.
        Assert.All(report.Windows, window => Assert.True(window.Simulated));
        var profitable = Assert.Single(report.Windows, window => window.IndependentScenarioNetPnl > 0);
        Assert.True(profitable.IndependentFlatRestart);
        Assert.True(profitable.StartUtc > report.Windows[31].EndUtc);
        Assert.Null(report.VerifiedNetPnl);
        var control = HistoricalGapAwareReplay.Compare(Date, report, report);
        Assert.Contains(control.Windows, window => window.StartUtc == profitable.StartUtc &&
            window.BaselineTrades == 1 && window.ProposalTrades == 1 && window.NetDifference == 0);
    }

    [Fact]
    public void StrategyComparisonUsesOnlyCommonStateAndWholePositionsBeforeEitherUncertainty()
    {
        HistoricalGapAwareWindow Window(int from, int until, bool reset, params HistoricalReplayFill[] fills) =>
            new(Start.AddSeconds(from), Start.AddSeconds(until), reset, true, 1, 0,
                fills.Select(fill => new HistoricalGapAwareFill(fill, null)).ToArray(), null, null);
        HistoricalReplayFill Fill(int decision, int exit, decimal net, bool partial = false) =>
            new("NIFTYCE", "test", Start.AddSeconds(decision), Start.AddSeconds(decision + 1),
                Start.AddSeconds(exit), 100, 110, 10, net + 2, 2, net, "ObservedTarget") { IsPartialExit = partial };
        var baseline = new HistoricalGapAwareMarket("NIFTY", 1, 100, true, [],
            [Window(0, 120, false, Fill(5, 20, 25), Fill(40, 49, 8, true), Fill(40, 75, 27)),
             Window(140, 180, true, Fill(145, 150, 100))], null, []);
        var proposal = baseline with { Windows =
            [Window(0, 119, false, Fill(5, 20, 32)) with { ExecutionUncertainSinceUtc = Start.AddSeconds(60) },
             Window(141, 180, true, Fill(145, 150, 100))] };
        var result = HistoricalGapAwareReplay.Compare(Date, baseline, proposal);
        var window = Assert.Single(result.Windows);
        Assert.Equal(Start.AddSeconds(60), window.EndUtc);
        Assert.Equal(1, window.BaselineTrades);
        Assert.Equal(1, window.ProposalTrades);
        Assert.Equal(25, window.BaselineNet);
        Assert.Equal(32, window.ProposalNet);
        Assert.Equal(7, window.NetDifference);
    }

    [Fact]
    public void PaperComparisonDoesNotCountTradesOutsideTheOpeningWindow()
    {
        var end = Start.AddMinutes(4);
        var openingEnd = Start.AddMinutes(2);
        var market = new HistoricalGapAwareMarket("BANKNIFTY", 0, 0, true, [],
            [new(Start, openingEnd, false, true, 0, 0, [], null, null),
             new(openingEnd.AddSeconds(5), end, true, true, 0, 0, [], null, null)], null, []);
        FidelityTrade Trade(string symbol, int entry, int exit) => new(symbol,
            [new("Entry", Start.AddSeconds(entry), 30, 100), new("Exit", Start.AddSeconds(exit), 30, 110)]);
        var result = HistoricalGapAwareReplayService.CompareOpeningWindows([market],
            new([Trade("BANKNIFTY55000CE", 60, 90), Trade("BANKNIFTY55000CE", 100, 130),
                Trade("BANKNIFTY55000PE", 160, 180), Trade("NIFTY22000CE", 60, 90)], []));
        Assert.NotNull(result);
        Assert.Equal(1, result.PaperTrades);
        Assert.Equal("BANKNIFTY55000CE", Assert.Single(result.Rows).Symbol);
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
