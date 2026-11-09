using TradingSystem.Infrastructure.MarketData;
using TradingSystem.Infrastructure.Execution;
using TradingSystem.Infrastructure.Diagnostics;
using TradingSystem.Application.Execution;
using TradingSystem.Domain.Trading;
using System.Globalization;

namespace TradingSystem.ContractTests;

public sealed class TradingWorkspaceIsolationTests
{
    [Theory]
    [InlineData("nifty", 1800, 650, 6)]
    [InlineData("sensex", 800, 450, 4)]
    [InlineData("bank-nifty", 800, 450, 4)]
    public void SessionTotalsIncludeBothHeroZeroLegsAndPartialsExactlyOnce(
        string market, decimal realised, decimal unrealised, int trades)
    {
        var start = DateTimeOffset.Parse("2026-11-09T18:30:00Z", CultureInfo.InvariantCulture);
        var end = start.AddDays(1);
        var before = new PaperAutomationSnapshot("Scanning", true, "unchanged", start.AddHours(12),
            2, 1000m, 200m, null, null, null, null, null, null);
        MarketPaperPosition Position(string owner, string strategy, DateTimeOffset opened) => new(
            Guid.NewGuid(), owner, Guid.NewGuid(), Guid.NewGuid(), strategy, Direction.Buy, 100, 50, 10, 200, opened);
        var call = Position(market, "Hero Zero|pair|CE", start.AddHours(9));
        call.Close(70, 50, "TargetHit", start.AddHours(10)); // +1950 net
        var put = Position(market, "Hero Zero|pair|PE", start.AddHours(9));
        put.Mark(40, 10, start.AddHours(10)); // -1000 open
        var partial = Position(market, "Expansion Pair|pair2|CE", start.AddHours(9));
        partial.TakePartial(70, 50, 50, start.AddHours(10)); // +950 net realised
        partial.Mark(79, 10, start.AddHours(11)); // +1450 on the remaining 50 only
        var loss = Position(market, "Hero Zero|earlier|PE", start.AddHours(8));
        loss.Close(29, 0, "StopLossHit", start.AddHours(9)); // -2100 net
        var foreign = Position("another-market", "Hero Zero|foreign|CE", start);
        foreign.Mark(1000, 10, start.AddHours(1));
        var old = Position(market, "Hero Zero|old|CE", start.AddDays(-1));
        old.Close(1000, 0, "Closed", start.AddDays(-1).AddHours(1));
        var next = Position(market, "Hero Zero|next|CE", end);
        var rows = new[] { call, put, partial, loss, call, foreign, old, next };
        var result = EfTradingWorkspaceReader.IncludeSessionPositionTotals(before, market, rows, start, end);
        Assert.Equal(realised, result.RealisedPnl);
        Assert.Equal(unrealised, result.UnrealisedPnl);
        Assert.Equal(trades, result.TradesToday);
        Assert.Equal(before.Status, result.Status);
        Assert.Equal(before.StopLoss, result.StopLoss);
        Assert.Equal(before.ActiveSignalId, result.ActiveSignalId);
    }

    [Fact]
    public void NiftyLegacyRowsAreNotAddedAgainButZeroZeroLossesAreIncluded()
    {
        var start = DateTimeOffset.Parse("2026-11-09T18:30:00Z", CultureInfo.InvariantCulture);
        var snapshot = new PaperAutomationSnapshot("Scanning", true, "", start, 1, 1000, 0, null, null, null, null, null, null);
        var legacy = new MarketPaperPosition(Guid.NewGuid(), "nifty", Guid.NewGuid(), Guid.NewGuid(),
            "NiftyAutomation", Direction.Buy, 100, 50, 10, 200, start);
        legacy.Close(60, 0, "Closed", start.AddMinutes(1));
        var hz = new MarketPaperPosition(Guid.NewGuid(), "nifty", Guid.NewGuid(), Guid.NewGuid(),
            "Hero Zero|pair|PE", Direction.Buy, 100, 50, 10, 200, start);
        hz.Close(40, 25, "Closed", start.AddMinutes(1));
        var result = EfTradingWorkspaceReader.IncludeSessionPositionTotals(snapshot, "nifty", [legacy, hz], start, start.AddDays(1));
        Assert.Equal(-25, result.RealisedPnl);
        Assert.Equal(2, result.TradesToday);
        Assert.Equal(0, result.UnrealisedPnl);
    }

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
    public void OperationalReportsAcceptEveryActiveWorkspace(string market)
    {
        Assert.True(EfOperationalAutomationReportReader.IsSupportedMarket(market));
        Assert.False(EfOperationalAutomationReportReader.IsSupportedMarket("unknown"));
    }

    [Fact]
    public void RetiredBankexIsNotAnActiveWorkspace()
    {
        Assert.DoesNotContain(TradingMarketCatalog.All, market => market.Code == "bankex");
        Assert.False(EfOperationalAutomationReportReader.IsSupportedMarket("bankex"));
        Assert.Throws<ArgumentOutOfRangeException>(() => TradingMarketCatalog.Get("bankex"));
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
