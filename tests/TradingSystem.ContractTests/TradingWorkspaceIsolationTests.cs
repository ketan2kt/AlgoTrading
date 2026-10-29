using TradingSystem.Infrastructure.MarketData;
using TradingSystem.Infrastructure.Execution;
using TradingSystem.Infrastructure.Diagnostics;
using TradingSystem.Domain.Trading;
using System.Globalization;

namespace TradingSystem.ContractTests;

public sealed class TradingWorkspaceIsolationTests
{
    [Theory]
    [InlineData("Hero Zero|pair|CE", true)]
    [InlineData("Expansion Pair|pair|CE", true)]
    [InlineData("Expansion Pair|pair|PE", true)]
    [InlineData("NiftyAutomation", false)]
    public void NiftyWorkspaceIncludesPairTradesWithoutDuplicatingLegacyTrades(
        string strategy, bool expected)
    {
        Assert.Equal(expected, EfTradingWorkspaceReader.IncludeNiftyMarketPosition(strategy));
    }

    [Fact]
    public void MonthlyBankOptionsRemainSelectableBeyondTenDays()
    {
        Assert.Equal(45, MultiMarketPaperTradingService.OptionExpiryHorizonDays(
            TradingMarketCatalog.BankNifty));
        Assert.Equal(45, MultiMarketPaperTradingService.OptionExpiryHorizonDays(
            TradingMarketCatalog.Bankex));
        Assert.Equal(10, MultiMarketPaperTradingService.OptionExpiryHorizonDays(
            TradingMarketCatalog.Sensex));
        Assert.Equal(new DateOnly(2026, 10, 30),
            MultiMarketPaperTradingService.BankRegularOptionStartDate(
                new DateOnly(2026, 10, 29)));
    }

    [Fact]
    public void SensexRegularEntriesUseNextContractOnExpiryDay()
    {
        var session = new DateOnly(2026, 10, 29);
        Assert.Equal(session.AddDays(1),
            MultiMarketPaperTradingService.RegularExecutionSearchDate(
                TradingMarketCatalog.Sensex, session, session));
        Assert.Equal(session,
            MultiMarketPaperTradingService.RegularExecutionSearchDate(
                TradingMarketCatalog.Sensex, session, session.AddDays(7)));
        Assert.Equal(session,
            MultiMarketPaperTradingService.RegularExecutionSearchDate(
                TradingMarketCatalog.Nifty, session, session));
    }

    [Theory]
    [InlineData("all")]
    [InlineData("nifty")]
    [InlineData("sensex")]
    [InlineData("bank-nifty")]
    [InlineData("bankex")]
    public void OperationalReportsAcceptEveryActiveWorkspace(string market)
    {
        Assert.True(EfOperationalAutomationReportReader.IsSupportedMarket(market));
        Assert.False(EfOperationalAutomationReportReader.IsSupportedMarket("unknown"));
    }

    [Fact]
    public void ObservedExcursionIncludesKnownExitAlongsideIntratradeSamples()
    {
        var excursion = EfTradingWorkspaceReader.CalculateObservedExcursion(
            100m, 50, 90m, 120m, 110m);

        Assert.Equal(1000m, excursion.MaximumProfit);
        Assert.Equal(500m, excursion.MaximumLoss);
    }

    [Fact]
    public void ObservedExcursionUsesExitWhenEarlierSamplesAreMissing()
    {
        var excursion = EfTradingWorkspaceReader.CalculateObservedExcursion(
            100m, 50, null, null, 110m);

        Assert.Equal(500m, excursion.MaximumProfit);
        Assert.Equal(0m, excursion.MaximumLoss);
    }

    [Fact]
    public void ObservedExcursionDoesNotInventValuesForUnfilledTrade()
    {
        var excursion = EfTradingWorkspaceReader.CalculateObservedExcursion(
            null, 50, 90m, 120m, null);

        Assert.Null(excursion.MaximumProfit);
        Assert.Null(excursion.MaximumLoss);
    }

    [Fact]
    public void BoughtPutExcursionsFollowOptionPricesNotBearishSignalDirection()
    {
        var opened = new DateTimeOffset(2026, 10, 28, 8, 23, 6, TimeSpan.Zero);
        var signalId = Guid.NewGuid();
        var instrumentId = Guid.NewGuid();
        var samples = new[]
        {
            new PaperTradePriceSample(Guid.NewGuid(), signalId, instrumentId, 160.95m,
                opened.AddMinutes(4)),
            new PaperTradePriceSample(Guid.NewGuid(), signalId, instrumentId, 178m,
                opened.AddMinutes(10))
        };

        var daily = EfDailyTradingDiagnosticsReader.CalculateBoughtOptionExcursionPath(
            167m, 325, opened, 171.75m, opened.AddMinutes(12), samples);
        var workspace = EfTradingWorkspaceReader.CalculateObservedExcursion(
            167m, 325, 160.95m, 178m, 171.75m);

        Assert.Equal(3575m, daily.MaximumFavourableExcursion);
        Assert.Equal(1966.25m, daily.MaximumAdverseExcursion);
        Assert.Equal(daily.MaximumFavourableExcursion, workspace.MaximumProfit);
        Assert.Equal(daily.MaximumAdverseExcursion, workspace.MaximumLoss);
    }

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
    [InlineData("2026-08-28T03:45:00Z", true)]  // Friday, 09:15 IST
    [InlineData("2026-08-30T03:45:00Z", false)] // Sunday, 09:15 IST
    [InlineData("2026-08-28T03:44:00Z", false)] // Before the cash session
    [InlineData("2026-08-28T10:01:00Z", false)] // After the cash session
    public void CashWorkspaceRejectsWeekendAndOutsideSessionCandles(string timestamp, bool expected)
    {
        var result = EfTradingWorkspaceReader.IsTradableSessionTimestamp(
            DateTimeOffset.Parse(timestamp, CultureInfo.InvariantCulture),
            new TimeOnly(9, 15),
            new TimeOnly(15, 30));

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("2026-08-28T03:30:00Z", true)]  // Friday, 09:00 IST
    [InlineData("2026-08-28T18:00:00Z", true)]  // Friday, 23:30 IST
    [InlineData("2026-08-28T18:01:00Z", false)] // After the commodity session
    [InlineData("2026-08-30T12:00:00Z", false)] // Sunday
    public void CommodityWorkspaceRejectsWeekendAndOutsideSessionCandles(string timestamp, bool expected)
    {
        var result = EfTradingWorkspaceReader.IsTradableSessionTimestamp(
            DateTimeOffset.Parse(timestamp, CultureInfo.InvariantCulture),
            new TimeOnly(9, 0),
            new TimeOnly(23, 30));

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("sensex", "Sensex")]
    [InlineData("bank-nifty", "Bank Nifty")]
    [InlineData("bankex", "Bankex")]
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
