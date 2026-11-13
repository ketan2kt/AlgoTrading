using System.Text.Json;
using TradingSystem.Application.Execution;
using TradingSystem.Application.Regime;
using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Execution;

namespace TradingSystem.ContractTests;

public sealed class HistoricalNormalReplayRunStoreContractTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("sarthi-replay-runs-").FullName;

    [Fact]
    public async Task FullSyntheticSessionRunsRealNormalPoliciesAndFrozenCodeComparisonWithoutOriginalTrades()
    {
        using var builds = new FileHistoricalPolicyBuildStore(TimeProvider.System, Path.Combine(directory, "policies"));
        var store = new FileHistoricalNormalReplayRunStore(directory);
        var archive = new CompletedFixtureArchive();
        var baseline = Configuration() with { BankNiftyLots = 2, MarketExits = Configuration().MarketExits with { InvalidationEnabled = false },
            NiftyAdmission = Configuration().NiftyAdmission with { EntryCutoff = new(9, 47) } };
        var request = new HistoricalNormalReplayComparisonRequest(CompletedFixtureArchive.Date, CompletedFixtureArchive.Date,
            baseline, baseline with { BankNiftyLots = 3 }, HistoricalReplayParameters.Default with
                { MaximumHoldMinutes = 375, MaximumConcurrentPositions = 10, AllowModeledHistoricalCosts = true });
        var result = await new HistoricalNormalReplayComparisonService(archive, builds, store).CompareAsync(request, CancellationToken.None);
        var day = Assert.Single(result.Comparison.Days);
        Assert.Equal(3, archive.ReadCount);
        Assert.NotEmpty(day.Baseline.CompletedPositions);
        Assert.All(day.Baseline.CompletedPositions, value => Assert.Equal("BANKNIFTY55000PE", value.Symbol));
        Assert.NotEqual(day.Baseline.NetPnl, day.Proposal.NetPnl);
        Assert.Equal(0, day.Baseline.MissingEntryQuoteCount);
        Assert.Equal(0, day.Baseline.MissingExitQuoteCount);
        // A synthetic fixture validates plumbing, never historical profitability
        // or an unearned production-parity certificate.
        Assert.Equal("InsufficientCoverage", result.Comparison.EvidenceStatus);
        var rerun = await new HistoricalNormalReplayComparisonService(new ForbiddenArchive(), builds, store).CompareAsync(
            request with { FrozenInputRunId = result.SavedRunId }, CancellationToken.None);
        Assert.Equal(JsonSerializer.Serialize(result.Comparison), JsonSerializer.Serialize(rerun.Comparison));
    }

    private sealed class CompletedFixtureArchive : IHistoricalReplayArchiveReader
    {
        public static readonly DateOnly Date = new(2026, 11, 4);
        public int ReadCount { get; private set; }
        public Task<HistoricalReplayArchiveSlice> ReadAsync(DateOnly date, string market, CancellationToken token)
        {
            ReadCount++;
            var symbol = market switch { "nifty" => "NIFTY", "sensex" => "SENSEX", _ => "BANKNIFTY" };
            var price = symbol == "NIFTY" ? 22000m : symbol == "SENSEX" ? 73000m : 55000m;
            var start = new DateTimeOffset(2026, 11, 4, 3, 45, 0, TimeSpan.Zero);
            var bars = new List<HistoricalReplayBar>();
            foreach (var dayOffset in new[] { -1, 0 })
            for (var minute = 0; minute < 375; minute++)
            {
                var at = start.AddDays(dayOffset).AddMinutes(minute);
                var open = symbol == "BANKNIFTY" && dayOffset == 0 ? price - .35m * Math.Min(minute, 30) : price;
                var close = symbol == "BANKNIFTY" && dayOffset == 0 && minute < 30 ? open - .245m : open;
                if (symbol == "BANKNIFTY" && dayOffset == 0 && minute == 30) close = 54988.5m;
                bars.Add(new(at, 60, open, open + .15m, Math.Min(open, close) - .15m, close) { Symbol = symbol });
                if (symbol != "BANKNIFTY") bars.Add(new(at, 60, price, price + .15m, price - .15m, price)
                    { Symbol = symbol + "-FUT", Volume = 100 });
            }
            var option = symbol + (symbol == "BANKNIFTY" ? "55000PE" : "CE");
            var quotes = new Dictionary<(string Symbol, DateTimeOffset At), HistoricalReplayQuote>();
            for (var second = 0; second <= 22500; second += 5)
            {
                var at = start.AddSeconds(second);
                quotes[(symbol, at)] = new(symbol, at, price);
                quotes[(option, at)] = Book(at, 100m);
            }
            if (symbol == "BANKNIFTY")
            foreach (var (second, premium) in new[] { (2, 100m), (4, 100m), (8, 120m), (10, 119m), (12, 105m), (14, 104m) })
            {
                var at = start.AddMinutes(31).AddSeconds(second);
                quotes[(option, at)] = Book(at, premium);
            }

            HistoricalReplayQuote Book(DateTimeOffset at, decimal premium) => new(option, at, premium)
            {
                Source = "GrowwFeedSample", DepthSourceAtUtc = at, SourceAtUtc = at,
                BidPrice = premium - .5m, OfferPrice = premium, BidQuantity = 1000, OfferQuantity = 1000,
                Volume = 1000, OpenInterest = 5000
            };
            var contracts = new[] {
                new NiftyOptionContractCandidate(Guid.NewGuid(), option,
                    symbol == "BANKNIFTY" ? InstrumentType.PutOption : InstrumentType.CallOption,
                    date.AddDays(7), price, symbol == "BANKNIFTY" ? 30 : 65, .05m),
                new NiftyOptionContractCandidate(Guid.NewGuid(), symbol + "-OTHER-SIDE",
                    symbol == "BANKNIFTY" ? InstrumentType.CallOption : InstrumentType.PutOption,
                    date.AddDays(7), price, symbol == "BANKNIFTY" ? 30 : 65, .05m) };
            var savedContracts = contracts.Select((c, i) => new HistoricalFeedInstrument(symbol,
                symbol == "SENSEX" ? "BSE" : "NSE", "FNO", i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                c.TradingSymbol, c.Type, c.ExpiryDate, c.StrikePrice, c.LotSize, c.TickSize)).ToArray();
            var session = new HistoricalReplaySessionData(date, symbol, bars, quotes.Values.ToArray(), start, start.AddMinutes(375))
            {
                RequiredInputsComplete = true, EligibleOptionUniverseComplete = true, ExecutableQuoteSidesComplete = true,
                StrategyInputsComplete = true, PolicyInputManifest = "SYNTHETIC-TEST-FIXTURE-NOT-MARKET-EVIDENCE",
                ExecutionOptionSymbols = [option], ExchangeCode = symbol == "SENSEX" ? "BSE" : "NSE"
            };
            return Task.FromResult(new HistoricalReplayArchiveSlice(session, price, symbol + "-FUT",
                contracts, []) { UnderlyingInstrumentId = Guid.NewGuid(),
                    RecordedContractUniverse = new(RegularOptionContractSelector.Version, savedContracts) });
        }
    }

    [Fact]
    public async Task FrozenInputsSettingsCodeAndReportSurviveCompressionAndRestart()
    {
        var date = new DateOnly(2026, 11, 3);
        var at = new DateTimeOffset(2026, 11, 3, 3, 45, 0, TimeSpan.Zero);
        var session = new HistoricalReplaySessionData(date, "NIFTY",
            [new(at, 60, 100, 102, 99, 101) { Symbol = "NIFTY" }],
            [new("CALL", at, 100) { BidPrice = 99, OfferPrice = 101 }], at, at.AddHours(6));
        var slice = new HistoricalReplayArchiveSlice(session, 100, "NIFTY-FUT", [], [])
            { UnderlyingInstrumentId = Guid.NewGuid() };
        var request = new HistoricalNormalReplayComparisonRequest(date, date, Configuration(), Configuration(),
            HistoricalReplayParameters.Default) { BaselineBuildId = "old", ProposalBuildId = "new" };
        var comparison = new HistoricalSharedPortfolioResult(new([session], []), [], "data", "run", 0, 0, "Preview", []);
        var report = new HistoricalNormalReplayComparisonReport(comparison, ["Normal only"])
            { BaselineBuildId = "old", ProposalBuildId = "new", ExecutionBuildId = "engine" };
        var run = new HistoricalNormalReplaySavedRun(at, "engine", request, [slice], report);
        var store = new FileHistoricalNormalReplayRunStore(directory);
        var id = await store.SaveAsync(run, CancellationToken.None);
        Assert.Equal(id, await store.SaveAsync(run, CancellationToken.None));
        var restarted = new FileHistoricalNormalReplayRunStore(directory);
        var restored = await restarted.LoadAsync(id, CancellationToken.None);
        Assert.Equal(JsonSerializer.Serialize(run), JsonSerializer.Serialize(restored));
        Assert.Equal(date, Assert.Single(restored.Report.Comparison.Cohort.IncludedDates));
        Assert.Empty(restored.Report.Comparison.Cohort.IncludedSessions);
        Assert.Equal(99, Assert.Single(Assert.Single(restored.Inputs).Session.Quotes).BidPrice);
        var summary = Assert.Single(await restarted.ListAsync(CancellationToken.None));
        Assert.Equal(id, summary.Id);
        Assert.Equal(1, summary.IncludedDays);
        Assert.Equal("old", summary.BaselineBuildId);
        Assert.Equal(JsonSerializer.Serialize(report), JsonSerializer.Serialize((await restarted.ViewAsync(id, CancellationToken.None)).Report));
        // Browsing a saved result never decompresses its raw market inputs.
        await File.AppendAllTextAsync(Path.Combine(directory, id + ".json.gz"), "broken input archive");
        Assert.Equal("old", (await restarted.ViewAsync(id, CancellationToken.None)).Request.BaselineBuildId);
        await Assert.ThrowsAsync<InvalidDataException>(() => restarted.LoadAsync(id, CancellationToken.None));
    }

    [Fact]
    public async Task SavedInputReuseNeverQueriesTheMutableDatabaseArchive()
    {
        var store = new FileHistoricalNormalReplayRunStore(directory);
        using var builds = new FileHistoricalPolicyBuildStore(TimeProvider.System, Path.Combine(directory, "policies"));
        var service = new HistoricalNormalReplayComparisonService(new MissingArchive(), builds, store);
        var date = new DateOnly(2026, 11, 3);
        var request = new HistoricalNormalReplayComparisonRequest(date, date, Configuration(), Configuration(), HistoricalReplayParameters.Default);
        var first = await service.CompareAsync(request, CancellationToken.None);
        Assert.NotNull(first.SavedRunId);
        var replay = new HistoricalNormalReplayComparisonService(new ForbiddenArchive(), builds, store);
        var next = await replay.CompareAsync(request with { FrozenInputRunId = first.SavedRunId }, CancellationToken.None);
        Assert.Equal(JsonSerializer.Serialize(first.Comparison), JsonSerializer.Serialize(next.Comparison));
        Assert.NotNull(next.SavedRunId);
        await Assert.ThrowsAsync<ArgumentException>(() => replay.CompareAsync(request with
            { From = date.AddDays(-1), FrozenInputRunId = first.SavedRunId }, CancellationToken.None));
    }

    [Fact]
    public async Task AlteredCompressedArchiveIsRejected()
    {
        var store = new FileHistoricalNormalReplayRunStore(directory);
        Directory.CreateDirectory(directory);
        var id = new string('A', 64);
        await File.WriteAllTextAsync(Path.Combine(directory, id + ".json.gz"), "corrupt");
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(id, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => store.LoadAsync("../outside", CancellationToken.None));
    }

    private sealed class MissingArchive : IHistoricalReplayArchiveReader
    {
        public Task<HistoricalReplayArchiveSlice> ReadAsync(DateOnly date, string market, CancellationToken token) =>
            throw new InvalidOperationException("Missing archive");
    }
    private sealed class ForbiddenArchive : IHistoricalReplayArchiveReader
    {
        public Task<HistoricalReplayArchiveSlice> ReadAsync(DateOnly date, string market, CancellationToken token) =>
            throw new NotSupportedException("Saved input reuse must not read the current archive.");
    }
    [Fact]
    public void ControlRejectsDifferentFillsEvenWhenNetAndTradeCountMatch()
    {
        var date = new DateOnly(2026, 11, 5);
        var request = new HistoricalNormalReplayComparisonRequest(date, date, Configuration(), Configuration(), HistoricalReplayParameters.Default);
        var at = new DateTimeOffset(2026, 11, 5, 4, 0, 0, TimeSpan.Zero);
        var trade = new HistoricalReplayFill("NIFTYCE","test",at,at,at.AddMinutes(1),100,101,1,1,0,1,"target");
        var a = new HistoricalReplayResult([trade],1,0,0,0,1,0,"test");
        var result = new HistoricalSharedPortfolioResult(new([],[]),[new(date,a,a)],"data","run",1,0,"Preview",[]);
        Assert.True(HistoricalExperimentReview.Assess(request,result,true).VerifiedControlWithFills);
        var different = a with { Trades = [trade with { ExitAtUtc = at.AddMinutes(2) }] };
        Assert.False(HistoricalExperimentReview.Assess(request,result with { Days=[new(date,a,different)] },true).VerifiedControlWithFills);
        Assert.False(HistoricalExperimentReview.Assess(request,result with { Days=[] },true).VerifiedControlWithFills);
    }

    private static HistoricalNormalPolicyConfiguration Configuration() => new(60, 4, 2,
        new(9, 30), 5, 5, 10m, new(),
        new(true, 1.5m, 500m, 100m, 100m, 10m, 1m, 8, 100_000m, 1_000_000m, 100_000m,
            new(9, 15), new(15, 0), new(15, 15), 5, 5, true, .35m, 1m, .6m),
        new(new(15, 15), true, true, true, true, .6m, true, .55m, 3, true, .8m, 5, 5, 60),
        new(new(9, 15), new(15, 0), new(15, 15), 5, 60, 10m, 5),
        new(new(15, 15), true, true, true, .55m, 3, true, .8m, 5, 5, 60),
        new MarketRegimeOptions(), new PriceActionStrategyOptions(), new OpeningRangeBreakoutOptions());

    public void Dispose() => Directory.Delete(directory, recursive: true);
}
