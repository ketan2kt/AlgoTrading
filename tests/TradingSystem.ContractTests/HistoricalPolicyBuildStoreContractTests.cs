using System.Text.Json;
using TradingSystem.Application.Execution;
using TradingSystem.Application.Regime;
using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Execution;

namespace TradingSystem.ContractTests;

public sealed class HistoricalPolicyBuildStoreContractTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("sarthi-policy-build-").FullName;

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
        var quotes = new[] { Quote(2, 100), Quote(4, 100), Quote(8, 120), Quote(10, 119), Quote(12, 105), Quote(14, 104) };
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
