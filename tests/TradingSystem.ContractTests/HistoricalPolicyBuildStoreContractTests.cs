using System.Text.Json;
using TradingSystem.Application.Execution;
using TradingSystem.Application.Regime;
using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Execution;

namespace TradingSystem.ContractTests;

public sealed class HistoricalPolicyBuildStoreContractTests(Xunit.Abstractions.ITestOutputHelper output) : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("sarthi-policy-build-").FullName;

    [Fact]
    public void LegacyModuleWithoutCompactExtensionStillReceivesCompleteFrames()
    {
        var legacy = new LegacyModule();
        var adapter = typeof(FileHistoricalPolicyBuildStore).GetNestedType("JsonPolicy", System.Reflection.BindingFlags.NonPublic)!;
        var policy = (IHistoricalReplayPortfolioPolicy)Activator.CreateInstance(adapter, typeof(LegacyModule), legacy)!;
        var at = new DateTimeOffset(2026, 11, 16, 10, 0, 0, TimeSpan.FromHours(5.5));
        var frame = new HistoricalReplayPortfolioFrame(new(at,
            new HistoricalReplayBar[] { new(at.AddMinutes(-1), 60, 100, 101, 99, 100) { Symbol = "NIFTY" } },
            new Dictionary<string, HistoricalReplayQuote>()), null, []);
        Assert.Empty(policy.Evaluate(frame));
        Assert.Empty(policy.EvaluateStopUpdates(frame));
        Assert.Equal(2, legacy.Calls);
    }

    public sealed class LegacyModule
    {
        public int Calls { get; private set; }
        public string Query(string operation, string json, string? subject)
        {
            Calls++;
            Assert.Single(JsonSerializer.Deserialize<HistoricalReplayPortfolioFrame>(json)!.Market.CompletedBars);
            return "[]";
        }
    }

    [Theory]
    [InlineData("NIFTY")]
    [InlineData("SENSEX")]
    [InlineData("BANKNIFTY")]
    public void CompactPolicyTransportMatchesFullFramesAndRejectsFutureBars(string market)
    {
        var start = new DateTimeOffset(2026, 11, 16, 9, 15, 0, TimeSpan.FromHours(5.5));
        var at = start.AddMinutes(350);
        var symbol = market + "CE";
        var slice = new HistoricalReplayArchiveSlice(new(new(2026, 11, 16), market, [], [], start, start.AddMinutes(375)),
            55000, market + "FUT", [new(Guid.NewGuid(), symbol, InstrumentType.CallOption, new(2026, 11, 23), 55000, 30, .05m)], [])
            { UnderlyingInstrumentId = Guid.NewGuid() };
        HistoricalPolicyModule Create() => new(JsonSerializer.Serialize(Configuration()), JsonSerializer.Serialize(slice));
        var bars = Enumerable.Range(-375, 725).Select(i => new HistoricalReplayBar(start.AddMinutes(i), 60,
            55000 + i, 55002 + i, 54999 + i, 55001 + i) { Symbol = market }).ToArray();
        var position = new HistoricalReplayOpenPosition(symbol, "Index momentum breakout", start.AddMinutes(320),
            100, 107, 150, 90, 120) { UnderlyingDirection = Direction.Buy, DecisionAtUtc = start.AddMinutes(320),
                InitialStopPrice = 90, InitialTargetPrice = 120, OriginalQuantity = 150, LotSize = 30,
                SignalConfidence = .8m, SignalRegime = MarketRegime.StrongBullishTrend, HighestDecisionPrice = 107 };
        var frames = Enumerable.Range(0, 1200).Select(i =>
        {
            var now = at.AddMilliseconds(i * 100);
            return new HistoricalReplayPortfolioFrame(new(now, bars, new Dictionary<string, HistoricalReplayQuote>
                { [market] = new(market, now, 55350), [symbol] = new(symbol, now, 107) }), position, [])
                { OpenPositions = [position] };
        }).ToArray();
        string[] Run(bool compact, out double ms, out long bytes)
        {
            var module = Create();
            var results = new List<string>();
            var barJson = JsonSerializer.Serialize(bars);
            var positionJson = JsonSerializer.Serialize(position);
            // Use the actual host encoder in both arms. The legacy arm already
            // caches encoded bars; do not benchmark against an artificial slow path.
            var converterType = typeof(FileHistoricalPolicyBuildStore).GetNestedType("CompletedBarFrameConverter",
                System.Reflection.BindingFlags.NonPublic)!;
            var converter = (System.Text.Json.Serialization.JsonConverter)Activator.CreateInstance(converterType, new object[] { compact })!;
            var options = new JsonSerializerOptions { Converters = { converter } };
            var warm = JsonSerializer.Serialize(frames[0], options);
            if (compact) module.QueryWithBars("stops", warm, positionJson, barJson);
            else module.Query("stops", warm, positionJson);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var before = GC.GetAllocatedBytesForCurrentThread();
            foreach (var frame in frames)
            {
                var json = JsonSerializer.Serialize(frame, options);
                foreach (var operation in new[] { "stops", "exits", "partials", "addons", "extend" })
                    results.Add(compact ? module.QueryWithBars(operation, json, positionJson, barJson)
                        : module.Query(operation, json, positionJson));
            }
            ms = watch.Elapsed.TotalMilliseconds;
            bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            return results.ToArray();
        }
        var full = Run(false, out var fullMs, out var fullBytes);
        var compact = Run(true, out var compactMs, out var compactBytes);
        Assert.Equal(full, compact);
        Assert.True(compactBytes < fullBytes / 2);
        output.WriteLine($"{market}: 1,200 real policy frames / 6,000 queries: full JSON {fullMs:F0} ms / {fullBytes / 1_000_000d:F1} MB; compact {compactMs:F0} ms / {compactBytes / 1_000_000d:F1} MB; identical decisions.");

        var test = Create();
        var input = JsonSerializer.Serialize(frames[0] with { Market = frames[0].Market with { CompletedBars = Array.Empty<HistoricalReplayBar>() } });
        Assert.Throws<ArgumentException>(() => test.QueryWithBars("stops", input, null,
            JsonSerializer.Serialize(bars.Append(new HistoricalReplayBar(at, 60, 100, 101, 99, 100) { Symbol = market }))));
        Assert.Throws<ArgumentException>(() => test.QueryWithBars("stops", JsonSerializer.Serialize(frames[0]), null, JsonSerializer.Serialize(bars)));
        // The same quote frame must observe a different completed-bar snapshot,
        // and legacy callers still work on the same module without state leakage.
        var reduced = bars.SkipLast(10).ToArray();
        Assert.Equal(Create().Query("stops", JsonSerializer.Serialize(frames[0] with
            { Market = frames[0].Market with { CompletedBars = reduced } }), null),
            test.QueryWithBars("stops", input, null, JsonSerializer.Serialize(reduced)));
        Assert.Equal(Create().Query("stops", JsonSerializer.Serialize(frames[0]), null),
            test.Query("stops", JsonSerializer.Serialize(frames[0]), null));
    }

    [Fact]
    public void ArchivedConstructorRejectsFutureSessionDataRatherThanExposeItToStrategyCode()
    {
        var at = new DateTimeOffset(2026, 11, 4, 3, 45, 0, TimeSpan.Zero);
        var slice = new HistoricalReplayArchiveSlice(new(new(2026, 11, 4), "BANKNIFTY",
            [new(at.AddHours(1), 60, 100, 101, 99, 100) { Symbol = "BANKNIFTY" }], [], at, at.AddHours(6)),
            100, "BANKNIFTY-FUT", [], []);
        Assert.Throws<ArgumentException>(() => new HistoricalPolicyModule(JsonSerializer.Serialize(Configuration()),
            JsonSerializer.Serialize(slice)));
    }

    [Fact]
    public async Task ApprovedBuildCaptureIsIdempotentAndVerifiesOnlyTwoPolicyAssemblies()
    {
        using var store = new FileHistoricalPolicyBuildStore(TimeProvider.System, directory);
        var first = await store.CaptureCurrentAsync(CancellationToken.None);
        var again = await store.CaptureCurrentAsync(CancellationToken.None);
        Assert.Equal(first.Id, again.Id);
        Assert.Equal(first.CapturedAtUtc, again.CapturedAtUtc);
        Assert.Equal(2, first.AssemblyHashes.Count);
        Assert.Equal(first.Id, Assert.Single(await store.ListAsync(CancellationToken.None)).Id);
        Assert.Equal(3, Directory.GetFiles(Path.Combine(directory, first.Id)).Length);
    }

    [Theory]
    [InlineData("NIFTY")]
    [InlineData("SENSEX")]
    [InlineData("BANKNIFTY")]
    public async Task ArchivedBuildUsesJsonBoundaryAndMatchesCurrentNormalPolicy(string market)
    {
        using var store = new FileHistoricalPolicyBuildStore(TimeProvider.System, directory);
        var build = await store.CaptureCurrentAsync(CancellationToken.None);
        using var module = await store.LoadAsync(build.Id, CancellationToken.None);
        var configuration = Configuration();
        var at = new DateTimeOffset(2026, 11, 3, 3, 45, 0, TimeSpan.Zero);
        var slice = new HistoricalReplayArchiveSlice(new(new(2026, 11, 3), market, [], [], at, at.AddHours(6)),
            22000m, market + "-FUT", [], [])
        {
            UnderlyingInstrumentId = Guid.NewGuid(),
            PriorSensexThesis = market != "SENSEX" ? null : new("v1", "frozen-test-bootstrap", new(2026, 11, 2),
                Direction.Buy, .75m, DirectionalThesisPhaseV1.Maintained, 10, 1, true, false, false, ["Known before the replay session"])
        };
        var current = new HistoricalNormalPolicyFactory(configuration).Create(slice);
        var archived = module.Create(configuration, slice);
        var frame = new HistoricalReplayPortfolioFrame(new(at, [],
            new Dictionary<string, HistoricalReplayQuote>()), null, []);
        Assert.Equal(current.CodeVersion, archived.CodeVersion);
        Assert.Equal(current.ConfigurationManifest, archived.ConfigurationManifest);
        Assert.Equal(current.ProductionPolicyParityVerified, archived.ProductionPolicyParityVerified);
        Assert.Equal(JsonSerializer.Serialize(current.Evaluate(frame)), JsonSerializer.Serialize(archived.Evaluate(frame)));
        Assert.Empty(archived.EvaluateStopUpdates(frame));
        Assert.Empty(archived.EvaluatePartialExits(frame));
        Assert.Empty(archived.EvaluateAddOns(frame));
        Assert.Empty(archived.EvaluateExits(frame));
        // A second instance must not borrow any state from the first run.
        Assert.Empty(module.Create(configuration, slice).Evaluate(frame));
    }

    [Fact]
    public async Task ArchivedRealEntryAdmissionPartialAndExitMatchCurrentBuildOnSameClock()
    {
        using var store = new FileHistoricalPolicyBuildStore(TimeProvider.System, directory);
        var build = await store.CaptureCurrentAsync(CancellationToken.None);
        using var module = await store.LoadAsync(build.Id, CancellationToken.None);
        var date = new DateOnly(2026, 11, 4);
        var start = new DateTimeOffset(2026, 11, 4, 4, 0, 0, TimeSpan.Zero);
        var bars = Enumerable.Range(0, 30).Select(index =>
        {
            var open = 55000m - .35m * index;
            var close = open - .245m;
            return new HistoricalReplayBar(start.AddMinutes(index), 60, open, open + .15m, close - .15m, close)
                { Symbol = "BANKNIFTY" };
        }).Append(new HistoricalReplayBar(start.AddMinutes(30), 60, 54989.5m, 54989.6m, 54988.3m, 54988.5m)
            { Symbol = "BANKNIFTY" }).Append(new HistoricalReplayBar(start.AddDays(-1), 60,
                55100m, 55105m, 55095m, 55100m) { Symbol = "BANKNIFTY" }).ToArray();
        var at = start.AddMinutes(31);
        const string symbol = "BANKNIFTY55000PE";
        HistoricalReplayQuote Quote(int second, decimal price) => new(symbol, at.AddSeconds(second), price)
            { BidPrice = price - .5m, OfferPrice = price };
        var quotes = new[] { Quote(2, 100), Quote(4, 100), Quote(8, 120), Quote(10, 119), Quote(12, 105), Quote(14, 104),
            new HistoricalReplayQuote("BANKNIFTY", at.AddSeconds(8), 54988.5m),
            new HistoricalReplayQuote("BANKNIFTY", at.AddSeconds(10), 54988.4m),
            new HistoricalReplayQuote("BANKNIFTY", at.AddSeconds(12), 54988.3m) };
        var slice = new HistoricalReplayArchiveSlice(new(date, "BANKNIFTY", bars, quotes, at, at.AddMinutes(1)),
            55000m, "BANKNIFTY-FUT", [new(Guid.NewGuid(), symbol, InstrumentType.PutOption,
                date.AddDays(20), 55000m, 30, .05m)], []) { UnderlyingInstrumentId = Guid.NewGuid() };
        var configuration = Configuration() with { MarketExits = Configuration().MarketExits with
            { TargetExtensionEnabled = true, InvalidationEnabled = false, MaximumOptionAgeSeconds = 90 } };
        HistoricalReplayResult Run(IHistoricalReplayPortfolioPolicy policy) => HistoricalPortfolioReplay.Run(bars,
            quotes, policy, new(.1m, .1m, 0m, 0m, 30, 0), at, at.AddMinutes(1));
        var original = Run(new HistoricalNormalPolicyFactory(configuration).Create(slice));
        var archived = Run(module.Create(configuration, slice));
        Assert.Equal(2, original.Trades.Count);
        Assert.Contains(original.Trades, value => value.IsPartialExit);
        Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(archived));
    }

    [Fact]
    public async Task TamperedAssemblyIsRejectedRatherThanFallingBackToCurrentCode()
    {
        using var store = new FileHistoricalPolicyBuildStore(TimeProvider.System, directory);
        var build = await store.CaptureCurrentAsync(CancellationToken.None);
        await File.AppendAllTextAsync(Path.Combine(directory, build.Id, "TradingSystem.Application.dll"), "tampered");
        // A picker must not read old assemblies; execution must still reject them.
        Assert.Equal(build.Id, Assert.Single(await store.ListAsync(CancellationToken.None)).Id);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(build.Id, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.CaptureCurrentAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("C:\\outside")]
    [InlineData("")]
    public async Task BuildIdentityCannotBeAnArbitraryFilePath(string id)
    {
        using var store = new FileHistoricalPolicyBuildStore(TimeProvider.System, directory);
        await Assert.ThrowsAsync<ArgumentException>(() => store.LoadAsync(id, CancellationToken.None));
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
