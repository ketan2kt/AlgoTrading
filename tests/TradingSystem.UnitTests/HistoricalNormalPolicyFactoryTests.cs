using System.Text.Json;
using TradingSystem.Application.Execution;
using TradingSystem.Application.Regime;
using TradingSystem.Application.Strategies;

namespace TradingSystem.UnitTests;

public sealed class HistoricalNormalPolicyFactoryTests
{
    [Fact]
    public void FactoryBuildsAllThreeNormalMarketsWithCompiledVersionIdentity()
    {
        var date = new DateOnly(2026, 11, 3);
        var at = new DateTimeOffset(2026, 11, 3, 3, 45, 0, TimeSpan.Zero);
        var factory = new HistoricalNormalPolicyFactory(Configuration());
        foreach (var market in new[] { "NIFTY", "SENSEX", "BANKNIFTY" })
        {
            var slice = new HistoricalReplayArchiveSlice(new(date, market, [], [], at, at.AddHours(6)),
                22000m, market + "-FUT", [], []) { UnderlyingInstrumentId = Guid.NewGuid() };
            var policy = factory.Create(slice);
            Assert.Contains("|module:", policy.CodeVersion);
            Assert.NotEmpty(policy.ConfigurationManifest);
            Assert.True(policy.ProductionPolicyParityVerified);
            Assert.Empty(policy.Evaluate(new(new(at, [], new Dictionary<string, HistoricalReplayQuote>()), null, [])));
        }
    }

    [Fact]
    public async Task MissingDatesAreExcludedForBothVersionsRatherThanSilentlyOmitted()
    {
        var service = new HistoricalNormalReplayComparisonService(new MissingArchive());
        var request = new HistoricalNormalReplayComparisonRequest(new(2026, 11, 2), new(2026, 11, 3),
            Configuration(), Configuration(), HistoricalReplayParameters.Default);
        var report = await service.CompareAsync(request, CancellationToken.None);
        Assert.Empty(report.Comparison.Days);
        Assert.Equal(2, report.Comparison.Cohort.ExcludedDates.Count);
        Assert.Equal("InsufficientCoverage", report.Comparison.EvidenceStatus);
        Assert.Contains(report.Limitations, value => value.Contains("Hero Zero", StringComparison.Ordinal));
    }

    [Fact]
    public void PublicCohortSummaryDoesNotSerializeRawMarketArchives()
    {
        var date = new DateOnly(2026, 11, 3);
        var at = new DateTimeOffset(2026, 11, 3, 3, 45, 0, TimeSpan.Zero);
        var cohort = new HistoricalReplayCohortSelection([new(date, "NIFTY", [], [], at, at.AddHours(6))], []);
        var json = JsonSerializer.Serialize(cohort);
        Assert.DoesNotContain("IncludedSessions", json);
        Assert.Contains("IncludedDates", json);
        Assert.Equal(1, cohort.IncludedSessionCount);
    }

    private sealed class MissingArchive : IHistoricalReplayArchiveReader
    {
        public Task<HistoricalReplayArchiveSlice> ReadAsync(DateOnly tradingDate, string market,
            CancellationToken cancellationToken) => throw new InvalidOperationException("No archive");
    }

    private static HistoricalNormalPolicyConfiguration Configuration() => new(60, 4, 2,
        new(9, 30), 5, 5, 10m, new(),
        new(true, 1.5m, 500m, 100m, 100m, 10m, 1m, 8, 100_000m, 1_000_000m, 100_000m,
            new(9, 15), new(15, 0), new(15, 15), 5, 5, true, .35m, 1m, .6m),
        new(new(15, 15), true, true, true, true, .6m, true, .55m, 3, true, .8m, 5, 5, 60),
        new(new(9, 15), new(15, 0), new(15, 15), 5, 60, 10m, 5),
        new(new(15, 15), true, true, true, .55m, 3, true, .8m, 5, 5, 60),
        new MarketRegimeOptions(), new PriceActionStrategyOptions(), new OpeningRangeBreakoutOptions());
}
