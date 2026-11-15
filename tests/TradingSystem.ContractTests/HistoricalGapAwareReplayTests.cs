using Microsoft.Extensions.Logging.Abstractions;
using TradingSystem.Application.Broker;
using TradingSystem.Application.Execution;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Execution;
using TradingSystem.Infrastructure.MarketData;
using Xunit.Abstractions;

namespace TradingSystem.ContractTests;

public sealed class HistoricalGapAwareReplayTests(ITestOutputHelper output)
{
    [Fact]
    public void IndexOnlyBackupIsReportedWithoutInventingOptionBooks()
    {
        var rows = Enumerable.Range(0, 61).Select(second => Book(Start.AddSeconds(second), 100)).ToList();
        rows.AddRange(Enumerable.Range(0, 61).Select(second => new HistoricalReplayQuote("NIFTY", Start.AddSeconds(second), 24000)
            { Source = HistoricalQuoteObservation.UpstoxSnapshot, SourceAtUtc = Start.AddSeconds(second) }));
        var slice = Slice("NIFTY", rows.OrderBy(row => row.AvailableAtUtc).ToArray(), Start.AddMinutes(1)) with { BackupSamples = 61 };
        var report = HistoricalGapAwareReplay.RunIncludingBackup(slice, () => new BackupPolicy("NIFTYCE"));
        Assert.NotNull(report.UpstoxScenario);
        Assert.Equal(0, report.UpstoxScenario.SupplementalBooks);
        Assert.Equal(61, report.UpstoxScenario.SupplementalIndexSamples);
    }
    private static readonly DateOnly Date = new(2026, 11, 11);
    private static readonly DateTimeOffset Start = new(2026, 11, 11, 9, 15, 0, TimeSpan.FromHours(5.5));

    [Theory]
    [InlineData("NIFTY")]
    [InlineData("SENSEX")]
    [InlineData("BANKNIFTY")]
    public void CombinedSourceReplayReadsArchiveOnceAndMatchesSeparateRuns(string market)
    {
        var quotes = Enumerable.Range(0, 121).Select(second =>
        {
            var quote = Book(Start.AddSeconds(second), second >= 70 ? 120 : 100) with { Symbol = market + "CE" };
            return second is >= 45 and < 90 ? quote with
                { Source = HistoricalQuoteObservation.UpstoxSnapshot, DepthSourceAtUtc = null } : quote;
        }).ToArray();
        var path = new CountedPath(quotes);
        var slice = Slice(market, path, Start.AddMinutes(2)) with { BackupSamples = 45,
            MonitoredDepthGaps = [new(Start.AddSeconds(45), Start.AddSeconds(90), "primary outage")] };
        IHistoricalReplayPortfolioPolicy Create() => new BackupPolicy(market + "CE");
        var primary = HistoricalGapAwareReplay.Run(slice, Create);
        var backup = HistoricalGapAwareReplay.RunWithBackup(slice, Create);
        path.Reads = 0;
        var progress = new List<HistoricalReplayTimingSample>();
        var combined = HistoricalGapAwareReplay.RunIncludingBackup(slice, Create, timing: new(progress.Add));
        Assert.Equal(1, path.Reads);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(primary),
            System.Text.Json.JsonSerializer.Serialize(combined with { UpstoxScenario = null }));
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(backup), System.Text.Json.JsonSerializer.Serialize(combined.UpstoxScenario));
        Assert.Contains(progress, item => item.Stage == "upstox-assisted-start");
        Assert.Contains(progress, item => item.Stage == "upstox-assisted-complete");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => HistoricalGapAwareReplay.RunIncludingBackup(slice, Create, cancellationToken: cancelled.Token));
    }

    private sealed class CountedPath(IReadOnlyList<HistoricalReplayQuote> rows) : IHistoricalChronologicalQuotePath
    {
        public int Reads { get; set; }
        public int Count => rows.Count;
        public HistoricalReplayQuote this[int index] => rows[index];
        public IEnumerator<HistoricalReplayQuote> GetEnumerator() { Reads++; return rows.GetEnumerator(); }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public void LongPrimaryDepthOutageDoesNotCountStaleRowsAgainstFreshReplayMemoryBudget()
    {
        var quotes = Enumerable.Range(0, 121).Select(second => Book(Start.AddSeconds(second), second >= 70 ? 120 : 100)
            with { Source = HistoricalQuoteObservation.UpstoxSnapshot, DepthSourceAtUtc = null }).ToArray();
        var path = new StalePrefixPath(quotes);
        var slice = Slice("NIFTY", path, Start.AddMinutes(2)) with { BackupSamples = quotes.Length };
        var report = HistoricalGapAwareReplay.RunIncludingBackup(slice, () => new BackupPolicy("NIFTYCE"));
        Assert.Equal(1, path.Reads);
        Assert.Equal(0, report.FreshBookSamples);
        Assert.True(Assert.Single(report.UpstoxScenario!.Windows).ModeledNetPnl > 0);
    }

    private sealed class StalePrefixPath(IReadOnlyList<HistoricalReplayQuote> fresh) : IHistoricalChronologicalQuotePath
    {
        private const int StaleCount = 2_000_001;
        public int Reads { get; private set; }
        public int Count => StaleCount + fresh.Count;
        public HistoricalReplayQuote this[int index] => throw new NotSupportedException();
        public IEnumerator<HistoricalReplayQuote> GetEnumerator()
        {
            Reads++;
            // Price is current but depth is stale: this is the harder case.
            var stale = Book(Start, 100) with { DepthSourceAtUtc = Start.AddSeconds(-31) };
            for (var index = 0; index < StaleCount; index++) yield return stale;
            foreach (var quote in fresh) yield return quote;
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Theory]
    [InlineData("NIFTY")]
    [InlineData("SENSEX")]
    [InlineData("BANKNIFTY")]
    public async Task FullDayDualSourceArchiveHasIdenticalResultsWithOneDecode(string market)
    {
        var directory = Directory.CreateTempSubdirectory("replay-dual-source-load-").FullName;
        try
        {
            var symbols = Enumerable.Range(0, 16).Select(i => market + (i == 0 ? "CE" : $"CE{i:D2}"))
                .Order(StringComparer.Ordinal).ToArray();
            async IAsyncEnumerable<HistoricalReplayQuote> Rows()
            {
                for (var second = 0; second <= 22500; second += 3)
                    foreach (var symbol in symbols)
                    {
                        var quote = Book(Start.AddSeconds(second), second >= 70 ? 120m : 100m) with { Symbol = symbol };
                        yield return second % 1800 is >= 45 and < 90 ? quote with
                            { Source = HistoricalQuoteObservation.UpstoxSnapshot, DepthSourceAtUtc = null } : quote;
                    }
                await Task.CompletedTask;
            }
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var path = await FileHistoricalReplayQuotePath.BuildOrderedAsync(directory, Rows(), CancellationToken.None);
            var preparationMs = clock.Elapsed.TotalMilliseconds;
            var counted = new CountedPath(path);
            var slice = Slice(market, counted, Start.AddMinutes(375)) with { BackupSamples = 13 * 15 * symbols.Length };
            slice = slice with { RecordedContractUniverse = new("test", symbols.Select(symbol =>
                slice.RecordedContractUniverse!.Instruments[0] with { TradingSymbol = symbol }).ToArray()) };
            IHistoricalReplayPortfolioPolicy Create() => new BackupPolicy(market + "CE");
            clock.Restart();
            var primary = HistoricalGapAwareReplay.Run(slice, Create);
            var assisted = HistoricalGapAwareReplay.RunWithBackup(slice, Create);
            var separateMs = clock.Elapsed.TotalMilliseconds;
            Assert.Equal(2, counted.Reads);
            counted.Reads = 0;
            var stages = new List<HistoricalReplayTimingSample>();
            clock.Restart();
            var combined = HistoricalGapAwareReplay.RunIncludingBackup(slice, Create, timing: new(stages.Add));
            var combinedMs = clock.Elapsed.TotalMilliseconds;
            Assert.Equal(1, counted.Reads);
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(primary),
                System.Text.Json.JsonSerializer.Serialize(combined with { UpstoxScenario = null }));
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(assisted), System.Text.Json.JsonSerializer.Serialize(combined.UpstoxScenario));
            Assert.True(Assert.Single(combined.UpstoxScenario!.Windows).ModeledNetPnl > 0);
            Assert.Equal(16, combined.ContractsWithFreshBooks);
            output.WriteLine($"{market}: {path.Count:N0} persisted quotes / full 375 minutes; archive preparation {preparationMs:F0} ms; separate replay {separateMs:F0} ms; shared replay {combinedMs:F0} ms; fills, costs and gaps identical.");
            output.WriteLine(stages.Last().Totals);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("NIFTY")]
    [InlineData("SENSEX")]
    [InlineData("BANKNIFTY")]
    public async Task SimultaneousRecoveryKeepsBothSourcesAndPrefersPrimary(string market)
    {
        var directory = Directory.CreateTempSubdirectory("replay-source-collision-").FullName;
        try
        {
            // A contradictory backup mark must not replace the fresh Groww
            // observation or make freezing the complete session fail.
            var quotes = Enumerable.Range(0, 121).Select(second =>
                Book(Start.AddSeconds(second), second >= 70 ? 120 : 100) with { Symbol = market + "CE" }).ToArray();
            async IAsyncEnumerable<HistoricalReplayQuote> Rows()
            {
                foreach (var quote in quotes)
                {
                    yield return quote;
                    yield return quote with { Source = HistoricalQuoteObservation.UpstoxSnapshot, DepthSourceAtUtc = null,
                        LastPrice = 10, BidPrice = 9, OfferPrice = 11 };
                }
                await Task.CompletedTask;
            }
            var path = await FileHistoricalReplayQuotePath.BuildOrderedAsync(directory, Rows(), CancellationToken.None);
            Assert.Equal(242, path.Count);
            var slice = Slice(market, path, Start.AddMinutes(2)) with { BackupSamples = 121 };
            var combined = HistoricalGapAwareReplay.RunIncludingBackup(slice, () => new BackupPolicy(market + "CE"));
            Assert.Null(combined.UpstoxScenario); // All backup observations are redundant.
            Assert.True(combined.VerifiedNetPnl > 0);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("NIFTY")]
    [InlineData("SENSEX")]
    [InlineData("BANKNIFTY")]
    public void UpstoxBridgesModeledOutageWithoutChangingVerifiedPnl(string market)
    {
        var end = Start.AddMinutes(2);
        var outage = new HistoricalReplayGap(Start.AddSeconds(45), Start.AddSeconds(90), "primary outage");
        var quotes = Enumerable.Range(0, 121).Select(second =>
        {
            var book = Book(Start.AddSeconds(second), second >= 70 ? 120m : 100m) with { Symbol = market + "CE" };
            return second is >= 45 and < 90 ? book with { Source = HistoricalQuoteObservation.UpstoxSnapshot, DepthSourceAtUtc = null } : book;
        }).ToArray();
        var slice = Slice(market, quotes, end) with { MonitoredDepthGaps = [outage] };
        var session = slice.Session;
        if (market == "SENSEX") session = session with { ExchangeCode = "BSE" };
        slice = slice with { Session = session };
        IHistoricalReplayPortfolioPolicy Create() => new BackupPolicy(market + "CE");
        var verified = HistoricalGapAwareReplay.Run(slice, Create);
        Assert.NotEmpty(verified.Gaps);
        Assert.Null(verified.VerifiedNetPnl);
        var modeled = Assert.IsType<HistoricalBackupReplayScenario>(HistoricalGapAwareReplay.RunWithBackup(slice, Create));
        Assert.True(modeled.SupplementalBooks > 30);
        Assert.Empty(modeled.RemainingGaps);
        Assert.True(Assert.Single(modeled.Windows).ModeledNetPnl > 0m);

        // One broker sample inside the outage can't claim a continuous path.
        var thin = slice with { Session = session with { Quotes = quotes.Where(q =>
            q.Source != HistoricalQuoteObservation.UpstoxSnapshot || q.AvailableAtUtc == Start.AddSeconds(60)).ToArray() } };
        Assert.NotEmpty(HistoricalGapAwareReplay.RunWithBackup(thin, Create)!.RemainingGaps);

        // A completely missing mapped contract cannot be hidden by the
        // healthy backup for another contract in the same index.
        var incomplete = slice with { RecordedContractUniverse = slice.RecordedContractUniverse! with
        {
            Instruments = [.. slice.RecordedContractUniverse!.Instruments,
                slice.RecordedContractUniverse.Instruments[0] with { TradingSymbol = market + "PE", Type = InstrumentType.PutOption }]
        } };
        Assert.NotEmpty(HistoricalGapAwareReplay.RunWithBackup(incomplete, Create)!.RemainingGaps);
    }

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ComparisonResumesBaselineOnlyWhenInputAndPolicyFingerprintsStillMatch(bool backup)
    {
        var contracts = new[] { InstrumentType.CallOption, InstrumentType.PutOption }.Select((type, index) =>
            new NiftyOptionContractCandidate(Guid.Empty, index == 0 ? "NIFTYCE" : "NIFTYPE", type,
                Date.AddDays(7), 100, 1, .05m)).ToArray();
        var quotes = Enumerable.Range(0, 121).Select(second =>
            Book(Start.AddSeconds(second), second is >= 70 and <= 75 ? 120 : 100)).Select(quote =>
                backup && quote.AvailableAtUtc >= Start.AddSeconds(45) && quote.AvailableAtUtc < Start.AddSeconds(90)
                    ? quote with { Source = HistoricalQuoteObservation.UpstoxSnapshot, DepthSourceAtUtc = null } : quote).ToArray();
        var initial = Slice("NIFTY", quotes, Start.AddMinutes(2)) with
        {
            GapAwarePolicyInputsReady = true,
            BackupSamples = backup ? 45 : 0,
            OptionContracts = contracts,
            RecordedContractUniverse = new(RegularOptionContractSelector.Version, contracts.Select((contract, index) =>
                new HistoricalFeedInstrument("NIFTY", "NSE", "FNO", index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    contract.TradingSymbol, contract.Type, contract.ExpiryDate, contract.StrikePrice, contract.LotSize, contract.TickSize)).ToArray())
        };
        var archive = new ReplayArchive(initial);
        var builds = new TestBuilds();
        var service = new HistoricalGapAwareReplayService(archive, builds);
        var settings = new HistoricalNormalPolicyConfiguration(60, 4, 2, new(9, 30), 5, 5, 10m, new(),
            new(true, 1.5m, 500m, 100m, 100m, 10m, 1m, 8, 100_000m, 1_000_000m, 100_000m,
                new(9, 15), new(15, 0), new(15, 15), 5, 5, true, .35m, 1m, .6m),
            new(new(15, 15), true, true, true, true, .6m, true, .55m, 3, true, .8m, 5, 5, 60),
            new(new(9, 15), new(15, 0), new(15, 15), 5, 60, 10m, 5),
            new(new(15, 15), true, true, true, .55m, 3, true, .8m, 5, 5, 60),
            new(), new(), new());
        var request = new HistoricalNormalReplayComparisonRequest(Date, Date, settings, settings,
            SessionPolicyReceipt.ComparisonExecution) { BaselineBuildId = "base", ProposalBuildId = "proposal" };
        HistoricalGapComparisonBaseline? checkpoint = null;
        Task Save(string market, HistoricalGapComparisonBaseline value, CancellationToken _) { checkpoint = value; return Task.CompletedTask; }
        Task<HistoricalGapComparisonBaseline?> Read(string market, CancellationToken _) => Task.FromResult(checkpoint);
        Task Continue(string phase, CancellationToken _) => Task.CompletedTask;
        static Task Interrupt(string phase, CancellationToken _) => phase.EndsWith("proposal", StringComparison.Ordinal)
            ? throw new OperationCanceledException() : Task.CompletedTask;

        await Assert.ThrowsAsync<OperationCanceledException>(() => service.CompareAsync(request, "nifty", Interrupt, default, Read, Save));
        Assert.NotNull(checkpoint);
        Assert.Equal(["base"], builds.Created);
        Assert.Equal(backup, checkpoint.AssistedResult is not null);
        var resumed = await service.CompareAsync(request, "nifty", Continue, default, Read, Save);
        Assert.Equal(["base", "proposal"], builds.Created);
        var market = Assert.Single(resumed.Markets);
        var window = Assert.Single(backup ? market.UpstoxComparison!.Windows : market.Windows);
        Assert.Equal(1, window.BaselineTrades);
        Assert.Equal(window.BaselinePositions, window.ProposalPositions);
        Assert.Equal(0m, window.NetDifference);
        Assert.True(window.BaselineCosts > 0);
        if (backup) Assert.DoesNotContain(market.Windows, value => value.BaselineTrades > 0);

        builds.Created.Clear();
        archive.Slice = initial with { Session = initial.Session with { Quotes = quotes.Select(quote => quote with { BidQuantity = 120 }).ToArray() } };
        await service.CompareAsync(request, "nifty", Continue, default, Read, Save);
        Assert.Equal(["base", "proposal"], builds.Created);
        builds.Created.Clear();
        await service.CompareAsync(request with { Baseline = settings with { NiftyLots = 2 } }, "nifty", Continue, default, Read, Save);
        Assert.Equal(["base", "proposal"], builds.Created);
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

    private sealed class BackupPolicy(string symbol) : IHistoricalReplayPortfolioPolicy
    {
        public string CodeVersion => "upstox-gap-test";
        public IReadOnlyList<HistoricalReplayCandidate> Evaluate(HistoricalReplayPortfolioFrame frame) =>
            frame.Market.AvailableAtUtc == Start.AddMinutes(1)
                ? [new(symbol, frame.Market.AvailableAtUtc, CodeVersion, .8m, 10) { LotSize = 1 }] : [];
    }

    private sealed class ReplayArchive(HistoricalReplayArchiveSlice slice) : IHistoricalReplayArchiveReader
    {
        public HistoricalReplayArchiveSlice Slice { get; set; } = slice;
        public Task<HistoricalReplayArchiveSlice> ReadAsync(DateOnly date, string market, CancellationToken token) => Task.FromResult(Slice);
    }

    private sealed class TestBuilds : IHistoricalPolicyBuildStore
    {
        public List<string> Created { get; } = [];
        public Task<HistoricalPolicyBuild> CaptureCurrentAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<IReadOnlyList<HistoricalPolicyBuild>> ListAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<IHistoricalPolicyModuleFactory> LoadAsync(string buildId, CancellationToken token) =>
            Task.FromResult<IHistoricalPolicyModuleFactory>(new Build(buildId, Created));
        private sealed class Build(string id, List<string> created) : IHistoricalPolicyModuleFactory
        {
            public string BuildId => id;
            public string? ContractSelectionVersion => RegularOptionContractSelector.Version;
            public IHistoricalReplayPortfolioPolicy Create(HistoricalNormalPolicyConfiguration settings, HistoricalReplayArchiveSlice slice)
            { created.Add(id); return new Policy(60); }
            public void Dispose() { }
        }
    }
}
