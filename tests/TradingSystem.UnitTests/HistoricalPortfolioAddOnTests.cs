using TradingSystem.Application.Execution;

namespace TradingSystem.UnitTests;

public sealed class HistoricalPortfolioAddOnTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 29,
        4, 0, 0, TimeSpan.Zero);
    private static readonly HistoricalReplayParameters Parameters =
        new(.10m, .90m, 0m, 0m, 10, 0);

    [Fact]
    public void WinnerAddOnFillsOnlyOnLaterQuoteAndChargesBothBuys()
    {
        var result = HistoricalPortfolioReplay.Run([Bar()], [
            Quote(2, 100m), Quote(3, 120m), Quote(4, 125m),
            Quote(5, 80m), Quote(6, 79m)
        ], new AddOnPolicy(), Parameters, Start, Start.AddMinutes(9));

        var fill = Assert.Single(result.Trades);
        Assert.Equal(100, fill.Quantity);
        Assert.Equal(100, fill.OriginalQuantity);
        Assert.Equal(106.25m, fill.EntryPrice);
        Assert.Equal(79m, fill.ExitPrice);
        Assert.Equal("ObservedStop", fill.ExitReason);
        Assert.Equal(Start.AddMinutes(6), fill.ExitAtUtc);
        Assert.Equal(0, result.MissingAddOnQuoteCount);
        Assert.Null(fill.ObservedPeakNetPnl);
        Assert.Null(fill.SampledProfitGiveback);
        var expectedCosts = PaperTradingCostModel.CalculateOptionCharges([
            new(PaperOptionTransactionSide.Buy, 100m, 75),
            new(PaperOptionTransactionSide.Buy, 125m, 25),
            new(PaperOptionTransactionSide.Sell, 79m, 100)
        ]).Total;
        Assert.Equal(expectedCosts, fill.Costs);
        Assert.Equal(fill.GrossPnl - expectedCosts, fill.NetPnl);
    }

    [Fact]
    public void StopOnAddOnQuoteCancelsBuyAndExitsBaseQuantity()
    {
        var result = HistoricalPortfolioReplay.Run([Bar()], [
            Quote(2, 100m), Quote(3, 120m), Quote(4, 89m),
            Quote(5, 88m)
        ], new AddOnPolicy(), Parameters, Start, Start.AddMinutes(9));

        var fill = Assert.Single(result.Trades);
        Assert.Equal(75, fill.Quantity);
        Assert.Equal(100m, fill.EntryPrice);
        Assert.Equal("ObservedStop", fill.ExitReason);
        Assert.Equal(0, result.MissingAddOnQuoteCount);
    }

    [Fact]
    public void PartialAfterFilledAddOnAllocatesBothEntryOrderCostsOnce()
    {
        var result = HistoricalPortfolioReplay.Run([Bar()], [
            Quote(2, 100m), Quote(3, 120m), Quote(4, 125m),
            Quote(5, 130m), Quote(6, 130m), Quote(7, 89m),
            Quote(8, 88m)
        ], new AddOnThenPartialPolicy(), Parameters,
            Start, Start.AddMinutes(10));

        Assert.Equal(2, result.Trades.Count);
        Assert.Equal([25, 75], result.Trades.Select(value => value.Quantity));
        Assert.Equal([true, false], result.Trades.Select(value => value.IsPartialExit));
        var completed = Assert.Single(result.CompletedPositions);
        Assert.Equal(100, completed.Quantity);
        Assert.Equal(result.NetPnl, completed.NetPnl);
        var expectedCosts = PaperTradingCostModel.CalculateOptionCharges([
            new(PaperOptionTransactionSide.Buy, 100m, 75),
            new(PaperOptionTransactionSide.Buy, 125m, 25)
        ]).Total + PaperTradingCostModel.CalculateOptionCharges([
            new(PaperOptionTransactionSide.Sell, 130m, 25)
        ]).Total + PaperTradingCostModel.CalculateOptionCharges([
            new(PaperOptionTransactionSide.Sell, 88m, 75)
        ]).Total;
        Assert.Equal(expectedCosts, result.TotalCosts);
    }

    private static HistoricalReplayBar Bar() => new(Start, 60,
        22000m, 22001m, 21999m, 22000m) { Symbol = "NIFTY" };

    private static HistoricalReplayQuote Quote(int minute, decimal price) =>
        new("NIFTY22000CE", Start.AddMinutes(minute), price);

    private sealed class AddOnPolicy : IHistoricalReplayPortfolioPolicy
    {
        public string CodeVersion => "add-on-test-v1";

        public IReadOnlyList<HistoricalReplayCandidate> Evaluate(
            HistoricalReplayPortfolioFrame frame) =>
            frame.Market.AvailableAtUtc == Start.AddMinutes(1)
                ? [new("NIFTY22000CE", frame.Market.AvailableAtUtc,
                    CodeVersion, .8m, 75) { LotSize = 25 }]
                : [];

        public IReadOnlyList<HistoricalReplayAddOn> EvaluateAddOns(
            HistoricalReplayPortfolioFrame frame) =>
            frame.Market.AvailableAtUtc == Start.AddMinutes(3)
                ? [new("NIFTY22000CE", frame.Market.AvailableAtUtc,
                    25, 1_000m, "WinnerAddOn")]
                : [];

        public IReadOnlyList<HistoricalReplayPartialExit> EvaluatePartialExits(
            HistoricalReplayPortfolioFrame frame) =>
            (frame.Market.AvailableAtUtc == Start.AddMinutes(3) ||
             frame.Market.AvailableAtUtc == Start.AddMinutes(4)) &&
            frame.OpenPosition is { } position &&
            position.LatestPrice > position.StopPrice
                ? [new("NIFTY22000CE", frame.Market.AvailableAtUtc,
                    25, "PartialProfit")]
                : [];
    }

    private sealed class AddOnThenPartialPolicy : IHistoricalReplayPortfolioPolicy
    {
        public string CodeVersion => "add-on-then-partial-test-v1";

        public IReadOnlyList<HistoricalReplayCandidate> Evaluate(
            HistoricalReplayPortfolioFrame frame) =>
            frame.Market.AvailableAtUtc == Start.AddMinutes(1)
                ? [new("NIFTY22000CE", frame.Market.AvailableAtUtc,
                    CodeVersion, .8m, 75) { LotSize = 25 }]
                : [];

        public IReadOnlyList<HistoricalReplayAddOn> EvaluateAddOns(
            HistoricalReplayPortfolioFrame frame) =>
            frame.Market.AvailableAtUtc == Start.AddMinutes(3)
                ? [new("NIFTY22000CE", frame.Market.AvailableAtUtc,
                    25, 1_000m, "WinnerAddOn")]
                : [];

        public IReadOnlyList<HistoricalReplayPartialExit> EvaluatePartialExits(
            HistoricalReplayPortfolioFrame frame) =>
            frame.Market.AvailableAtUtc == Start.AddMinutes(5)
                ? [new("NIFTY22000CE", frame.Market.AvailableAtUtc,
                    25, "PartialProfit")]
                : [];
    }
}
