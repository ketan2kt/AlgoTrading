using TradingSystem.Application.Diagnostics;

namespace TradingSystem.UnitTests;

public sealed class DailyCapitalSummaryTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 17, 4, 0, 0, TimeSpan.Zero);
    private static DailyCapitalTrade Position(string market, int minute, decimal price, int quantity,
        decimal exit, int duration = 10, decimal fees = 10, string strategy = "Core")
    {
        var at = Start.AddMinutes(minute);
        var gross = (exit - price) * quantity;
        var trade = new DailyTradingTrade(market, "MarketPaperPosition", Guid.NewGuid(), null,
            "TESTCE", strategy, "Buy", quantity, price, exit, at, at.AddMinutes(duration),
            "Closed", "ManualExit", gross, fees, gross - fees, null, null, null, null);
        return new(trade, [new(at, "Entry", quantity, price), new(at.AddMinutes(duration), "Exit", quantity, exit)]);
    }

    [Fact]
    public void CombinesAllThreeAndBothHeroZeroLegsAfterCosts()
    {
        var result = DailyCapitalSummaryBuilder.Build([
            Position("NIFTY", 0, 100, 10, 120), Position("SENSEX", 1, 200, 5, 190),
            Position("BANK-NIFTY", 2, 300, 5, 330),
            Position("NIFTY", 3, 10, 10, 30, strategy: "Hero Zero|basket|CE"),
            Position("NIFTY", 3, 15, 10, 0.5m, strategy: "Hero Zero|basket|PE")]);
        Assert.Equal("Verified", result.Status);
        Assert.Equal(305m, result.NetRealisedPnl);
        Assert.Equal(3750m, result.PeakCapitalUsed);
        Assert.Equal(Start.AddMinutes(3), result.PeakAtUtc);
        Assert.Equal(8.1333m, result.ReturnPercent);
    }

    [Fact]
    public void PartialExitReleasesCostNotSaleProceedsBeforeNextTrade()
    {
        var p = Position("BANK-NIFTY", 0, 100, 10, 120);
        p = p with { Trade = p.Trade with { GrossPnl = 240, NetRealisedPnl = 230 }, Events = [
            new(Start, "Entry", 10, 100), new(Start.AddMinutes(2), "Partial", 4, 130),
            new(Start.AddMinutes(10), "Exit", 6, 120)] };
        var result = DailyCapitalSummaryBuilder.Build([p, Position("SENSEX", 3, 100, 10, 100)]);
        Assert.Equal(1600m, result.PeakCapitalUsed);
        Assert.Equal(220m, result.NetRealisedPnl);
        Assert.Equal(13.75m, result.ReturnPercent);
    }

    [Fact]
    public void AddOnUsesActualTimeNotFinalQuantityAtOriginalEntry()
    {
        var p = Position("NIFTY", 0, 150, 20, 160);
        p = p with { Events = [new(Start, "Entry", 10, 100),
            new(Start.AddMinutes(5), "Add-on", 10, 200), new(Start.AddMinutes(10), "Exit", 20, 160)] };
        var result = DailyCapitalSummaryBuilder.Build([p, Position("SENSEX", 1, 200, 10, 200, duration: 3)]);
        Assert.Equal(3000m, result.PeakCapitalUsed); // not 5000 from a final-size overlap shortcut
        Assert.Equal(Start.AddMinutes(1), result.PeakAtUtc);
    }

    [Fact]
    public void ReusedCapitalAndCoincidentCloseOpenDoNotAddTurnover()
    {
        var result = DailyCapitalSummaryBuilder.Build([
            Position("NIFTY", 0, 100, 10, 90), Position("SENSEX", 10, 100, 10, 100)]);
        Assert.Equal(1000m, result.PeakCapitalUsed);
        Assert.Equal(-12m, result.ReturnPercent);
    }

    [Fact]
    public void MissingOrDuplicateReceiptsNeverYieldConfidentReturn()
    {
        var p = Position("NIFTY", 0, 100, 10, 110);
        Assert.Null(DailyCapitalSummaryBuilder.Build([p], missingEntries: true).PeakCapitalUsed);
        Assert.Null(DailyCapitalSummaryBuilder.Build([p with { EvidenceComplete = false }]).ReturnPercent);
        Assert.Null(DailyCapitalSummaryBuilder.Build([p with { Events = [p.Events[0]] }]).PeakCapitalUsed);
        Assert.Null(DailyCapitalSummaryBuilder.Build([p, p]).PeakCapitalUsed);
        Assert.Null(DailyCapitalSummaryBuilder.Build([p with { Events = [p.Events[0], p.Events[0], p.Events[1]] }]).PeakCapitalUsed);
    }

    [Fact]
    public void FinalFillReleasesCapitalBeforeResultPersistenceTimestamp()
    {
        var p = Position("NIFTY", 0, 100, 10, 110);
        p = p with { Trade = p.Trade with { ClosedAtUtc = Start.AddMinutes(12) } };
        var result = DailyCapitalSummaryBuilder.Build([p, Position("SENSEX", 11, 100, 10, 100)]);
        Assert.Equal("Verified", result.Status);
        Assert.Equal(1000m, result.PeakCapitalUsed);
    }

    [Fact]
    public void ClosedEconomicsMustReconcile()
    {
        var p = Position("NIFTY", 0, 100, 10, 110);
        Assert.Null(DailyCapitalSummaryBuilder.Build([p with { Trade = p.Trade with { GrossPnl = 500 } }]).ReturnPercent);
    }

    [Fact]
    public void OpenPositionCapitalCountsButNotUnrealisedProfit()
    {
        var p = Position("BANK-NIFTY", 0, 100, 10, 110);
        var result = DailyCapitalSummaryBuilder.Build([p with {
            Trade = p.Trade with { ClosedAtUtc = null }, Events = [p.Events[0]] }]);
        Assert.Equal(1000m, result.PeakCapitalUsed);
        Assert.Equal(0m, result.NetRealisedPnl);
        Assert.Equal(0m, result.ReturnPercent);
    }

    [Fact]
    public void EmptySessionHasZeroCapitalButNoPercentage()
    {
        var result = DailyCapitalSummaryBuilder.Build([]);
        Assert.Equal("NoTrades", result.Status);
        Assert.Equal(0m, result.PeakCapitalUsed);
        Assert.Null(result.ReturnPercent);
    }
}
