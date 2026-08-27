using TradingSystem.Infrastructure.MarketData;
using TradingSystem.Domain.Trading;
using System.Globalization;

namespace TradingSystem.ContractTests;

public sealed class TradingWorkspaceIsolationTests
{
    [Fact]
    public void SensexInstrumentLookupExcludesDuplicateSymbolsFromOtherSegments()
    {
        var now = DateTimeOffset.UtcNow;
        var cash = new Instrument(Guid.NewGuid(), "BSE", "SENSEX", InstrumentSegment.Cash,
            InstrumentType.Index, now);
        var derivative = new Instrument(Guid.NewGuid(), "BSE", "SENSEX", InstrumentSegment.FuturesAndOptions,
            InstrumentType.Index, now);

        var result = EfTradingWorkspaceReader.ScopeInstrumentQuery(
            new[] { cash, derivative }.AsQueryable(), TradingMarketCatalog.Sensex).ToArray();

        Assert.Equal([cash], result);
    }

    [Fact]
    public void NaturalGasInstrumentLookupUsesCommoditySegmentOnly()
    {
        var now = DateTimeOffset.UtcNow;
        var commodity = new Instrument(Guid.NewGuid(), "MCX", "NATGASMINI26AUGFUT",
            InstrumentSegment.Commodity, InstrumentType.Future, now);
        var wrongSegment = new Instrument(Guid.NewGuid(), "MCX", "NATGASMINI26AUGFUT",
            InstrumentSegment.FuturesAndOptions, InstrumentType.Future, now);

        var result = EfTradingWorkspaceReader.ScopeInstrumentQuery(
            new[] { commodity, wrongSegment }.AsQueryable(), TradingMarketCatalog.NaturalGas).ToArray();

        Assert.Equal([commodity], result);
    }

    [Fact]
    public void SensexLiveGridStartsAtCurrentIstSession()
    {
        var sessionStart = DateTimeOffset.Parse("2026-08-25T03:45:00Z", CultureInfo.InvariantCulture);

        var result = EfTradingWorkspaceReader.PositionHistoryStartUtc(
            TradingMarketCatalog.Sensex, sessionStart);

        Assert.Equal(sessionStart, result);
    }

    [Fact]
    public void NaturalGasLiveGridStartsAtCurrentIstSessionWhileDatabaseRetainsHistory()
    {
        var sessionStart = DateTimeOffset.Parse("2026-08-25T03:30:00Z", CultureInfo.InvariantCulture);

        var result = EfTradingWorkspaceReader.PositionHistoryStartUtc(
            TradingMarketCatalog.NaturalGas, sessionStart);

        Assert.Equal(sessionStart, result);
    }

    [Theory]
    [InlineData("sensex", "Sensex")]
    [InlineData("natural-gas", "Natural Gas Mini Futures")]
    public void UnavailableNonNiftyMarketNeverInheritsNiftyAutomation(
        string market,
        string expectedName)
    {
        var definition = TradingMarketCatalog.Get(market);
        var observedAt = DateTimeOffset.Parse("2026-08-20T06:00:00Z", CultureInfo.InvariantCulture);

        var snapshot = EfTradingWorkspaceReader.CreateUnavailableAutomation(
            definition,
            "InstrumentUnavailable",
            $"Synchronise the Groww {expectedName} instrument before enabling the feed.",
            observedAt);

        Assert.Equal("InstrumentUnavailable", snapshot.Status);
        Assert.False(snapshot.TradingPermitted);
        Assert.Equal(0, snapshot.TradesToday);
        Assert.Equal(0m, snapshot.RealisedPnl);
        Assert.Equal(0m, snapshot.UnrealisedPnl);
        Assert.Null(snapshot.PortfolioRisk);
        var check = Assert.Single(snapshot.ReadinessChecks!);
        Assert.Contains(expectedName, check.Label);
        Assert.False(check.Ready);
    }
}
