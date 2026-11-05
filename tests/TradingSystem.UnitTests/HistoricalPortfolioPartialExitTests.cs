using TradingSystem.Application.Execution;

namespace TradingSystem.UnitTests;

public sealed class HistoricalPortfolioPartialExitTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 29,
        4, 0, 0, TimeSpan.Zero);
    private static readonly HistoricalReplayParameters Parameters =
        new(.10m, 2m, 0m, 0m, 7, 0);

    [Fact]
    public void QueuedFullTargetExitSupersedesPartialFromSameTimestamp()
    {
        var result = HistoricalPortfolioReplay.Run([Bar(0)], [
            Quote(2, 100m), Quote(3, 130m), Quote(4, 129m)
        ], new PartialPolicy(30), new(.10m, .20m, 0m, 0m, 7, 0),
            Start, Start.AddMinutes(6));
        var closed = Assert.Single(result.Trades);
        Assert.False(closed.IsPartialExit);
        Assert.Equal(75, closed.Quantity);
        Assert.Equal("ObservedTarget", closed.ExitReason);
        Assert.Equal(Start.AddMinutes(4), closed.ExitAtUtc);
    }

    [Fact]
    public void PartialExitFillsOnLaterQuoteAndRemainderKeepsStop()
    {
        var policy = new PartialPolicy(30);
        var result = HistoricalPortfolioReplay.Run([Bar(0), Bar(4)], [
            Quote(2, 100m), Quote(3, 130m), Quote(4, 120m),
            Quote(5, 89m), Quote(6, 88m)
        ], policy, Parameters, Start, Start.AddMinutes(8));

        Assert.Equal(2, result.Trades.Count);
        var partial = result.Trades[0];
        Assert.True(partial.IsPartialExit);
        Assert.Equal(30, partial.Quantity);
        Assert.Equal(75, partial.OriginalQuantity);
        Assert.Equal(Start.AddMinutes(4), partial.ExitAtUtc);
        Assert.Equal("BankWinner", partial.ExitReason);
        var remainder = result.Trades[1];
        Assert.False(remainder.IsPartialExit);
        Assert.Equal(45, remainder.Quantity);
        Assert.Equal("ObservedStop", remainder.ExitReason);
        Assert.Equal(Start.AddMinutes(6), remainder.ExitAtUtc);
        Assert.Equal(45, policy.RemainingAtMinute5);
        Assert.Equal(1, policy.CompletedFillsAtMinute5);
        Assert.Equal(0, policy.ClosedPositionsAtMinute5);
        Assert.Single(result.CompletedPositions);
        Assert.Equal(result.NetPnl, result.CompletedPositions[0].NetPnl);
        Assert.Null(result.TotalSampledProfitGiveback);
        var expectedCosts = PaperTradingCostModel.CalculateOptionCharges([
            new(PaperOptionTransactionSide.Buy, 100m, 75)
        ]).Total + PaperTradingCostModel.CalculateOptionCharges([
            new(PaperOptionTransactionSide.Sell, 120m, 30)
        ]).Total + PaperTradingCostModel.CalculateOptionCharges([
            new(PaperOptionTransactionSide.Sell, 88m, 45)
        ]).Total;
        Assert.Equal(expectedCosts, result.TotalCosts);
        Assert.Equal(result.Trades.Sum(value => value.NetPnl), result.NetPnl);
    }

    [Fact]
    public void StaleQuoteCannotFillQueuedPartialOrInventRemainingPnl()
    {
        var result = HistoricalPortfolioReplay.Run([Bar(0)], [
            Quote(2, 100m), Quote(3, 130m), Quote(5, 120m)
        ], new PartialPolicy(30), Parameters, Start,
            Start.AddMinutes(8));

        Assert.Empty(result.Trades);
        Assert.Equal(1, result.MissingExitQuoteCount);
        Assert.True(result.StaleQuoteCount > 0);
    }

    [Fact]
    public void UnpricedRemainderDoesNotTurnPartialIntoCompletedTradeStatistics()
    {
        var result = HistoricalPortfolioReplay.Run([Bar(0)], [
            Quote(2, 100m), Quote(3, 130m), Quote(4, 120m),
            Quote(6, 125m)
        ], new PartialPolicy(30), Parameters, Start,
            Start.AddMinutes(8));

        Assert.True(Assert.Single(result.Trades).IsPartialExit);
        Assert.Empty(result.CompletedPositions);
        Assert.Equal(1, result.MissingExitQuoteCount);
        Assert.Equal(0m, result.Expectancy);
        Assert.Null(result.TotalSampledProfitGiveback);
    }

    [Fact]
    public void PartialRequestCannotConsumeEntirePosition()
    {
        Assert.Throws<InvalidOperationException>(() =>
            HistoricalPortfolioReplay.Run([Bar(0)], [
                Quote(2, 100m), Quote(3, 130m)
            ], new PartialPolicy(75), Parameters, Start,
                Start.AddMinutes(8)));
    }

    [Theory]
    [InlineData(31, 5)]
    [InlineData(30, 0)]
    public void PartialRequestRequiresDatedWholeLotMetadata(int quantity,
        int lotSize)
    {
        Assert.Throws<InvalidOperationException>(() =>
            HistoricalPortfolioReplay.Run([Bar(0)], [
                Quote(2, 100m), Quote(3, 130m)
            ], new PartialPolicy(quantity, lotSize), Parameters, Start,
                Start.AddMinutes(8)));
    }

    [Fact]
    public void TimeExitCancelsPendingPartialAndClosesWholePosition()
    {
        var result = HistoricalPortfolioReplay.Run([Bar(0)], [
            Quote(2, 100m), Quote(3, 130m),
            new HistoricalReplayQuote("NIFTY22000CE",
                Start.AddMinutes(4).AddSeconds(30), 120m)
        ], new PartialPolicy(30), Parameters with
        { MaximumHoldMinutes = 3 }, Start, Start.AddMinutes(6));

        var fill = Assert.Single(result.Trades);
        Assert.False(fill.IsPartialExit);
        Assert.Equal(75, fill.Quantity);
        Assert.Equal("TimeExit", fill.ExitReason);
    }

    [Fact]
    public void ObservedStopCancelsPendingPartialAndExitsWholePosition()
    {
        var optionStopBar = new HistoricalReplayBar(Start.AddMinutes(3),
            60, 100m, 130m, 85m, 120m) { Symbol = "NIFTY22000CE" };
        var result = HistoricalPortfolioReplay.Run([Bar(0), optionStopBar], [
            Quote(2, 100m), Quote(3, 130m), Quote(4, 120m),
            Quote(5, 95m)
        ], new PartialPolicy(30), Parameters, Start,
            Start.AddMinutes(8));

        var fill = Assert.Single(result.Trades);
        Assert.False(fill.IsPartialExit);
        Assert.Equal(75, fill.Quantity);
        Assert.Equal("ObservedBarStop", fill.ExitReason);
        Assert.Equal(Start.AddMinutes(5), fill.ExitAtUtc);
    }

    [Fact]
    public void SameQuoteStopPrecedesQueuedPartialFill()
    {
        var result = HistoricalPortfolioReplay.Run([Bar(0)], [
            Quote(2, 100m), Quote(3, 130m), Quote(4, 89m),
            Quote(5, 88m)
        ], new PartialPolicy(30), Parameters, Start,
            Start.AddMinutes(8));

        var fill = Assert.Single(result.Trades);
        Assert.False(fill.IsPartialExit);
        Assert.Equal(75, fill.Quantity);
        Assert.Equal("ObservedStop", fill.ExitReason);
        Assert.Equal(Start.AddMinutes(5), fill.ExitAtUtc);
    }

    [Fact]
    public void RepeatedPartialsAllocateEntryCostsOnceAndCountOneCompletedPosition()
    {
        var result = HistoricalPortfolioReplay.Run([Bar(0)], [
            Quote(2, 100m), Quote(3, 130m), Quote(4, 120m),
            Quote(5, 140m), Quote(6, 135m), Quote(7, 140m),
            Quote(8, 145m), new HistoricalReplayQuote("NIFTY22000CE",
                Start.AddMinutes(8).AddSeconds(30), 145m)
        ], new RepeatedPartialPolicy(), Parameters, Start,
            Start.AddMinutes(10));

        Assert.Equal([30, 20, 25], result.Trades.Select(value => value.Quantity));
        Assert.Equal([true, true, false], result.Trades.Select(value => value.IsPartialExit));
        var position = Assert.Single(result.CompletedPositions);
        Assert.Equal(75, position.Quantity);
        Assert.Equal(result.NetPnl, position.NetPnl);
        Assert.Equal(1, result.Wins);
        Assert.Equal(1m, result.WinRate);
        Assert.Equal(result.NetPnl, result.Expectancy);
        var expectedCosts = PaperTradingCostModel.CalculateOptionCharges([
            new(PaperOptionTransactionSide.Buy, 100m, 75)
        ]).Total + PaperTradingCostModel.CalculateOptionCharges([
            new(PaperOptionTransactionSide.Sell, 120m, 30)
        ]).Total + PaperTradingCostModel.CalculateOptionCharges([
            new(PaperOptionTransactionSide.Sell, 135m, 20)
        ]).Total + PaperTradingCostModel.CalculateOptionCharges([
            new(PaperOptionTransactionSide.Sell, 145m, 25)
        ]).Total;
        Assert.Equal(expectedCosts, result.TotalCosts);
    }

    private static HistoricalReplayBar Bar(int minute) => new(
        Start.AddMinutes(minute), 60, 22000m, 22001m, 21999m, 22000m)
    { Symbol = "NIFTY" };

    private static HistoricalReplayQuote Quote(int minute, decimal price) =>
        new("NIFTY22000CE", Start.AddMinutes(minute), price);

    private sealed class PartialPolicy(int quantity, int lotSize = 5)
        : IHistoricalReplayPortfolioPolicy
    {
        public string CodeVersion => "partial-test-v1";
        public int? RemainingAtMinute5 { get; private set; }
        public int? CompletedFillsAtMinute5 { get; private set; }
        public int? ClosedPositionsAtMinute5 { get; private set; }
        public IReadOnlyList<HistoricalReplayCandidate> Evaluate(
            HistoricalReplayPortfolioFrame frame)
        {
            if (frame.Market.AvailableAtUtc == Start.AddMinutes(5))
            {
                RemainingAtMinute5 = frame.OpenPosition?.Quantity;
                CompletedFillsAtMinute5 = frame.CompletedFills.Count;
                ClosedPositionsAtMinute5 = frame.ClosedTrades.Count;
            }
            return frame.Market.AvailableAtUtc == Start.AddMinutes(1)
                ? [new("NIFTY22000CE", frame.Market.AvailableAtUtc,
                    CodeVersion, .8m, 75) { LotSize = lotSize }] : [];
        }
        public IReadOnlyList<HistoricalReplayPartialExit> EvaluatePartialExits(
            HistoricalReplayPortfolioFrame frame) =>
            frame.Market.AvailableAtUtc == Start.AddMinutes(3)
                ? [new("NIFTY22000CE", frame.Market.AvailableAtUtc,
                    quantity, "BankWinner")] : [];
    }

    private sealed class RepeatedPartialPolicy : IHistoricalReplayPortfolioPolicy
    {
        public string CodeVersion => "repeated-partial-test-v1";
        public IReadOnlyList<HistoricalReplayCandidate> Evaluate(
            HistoricalReplayPortfolioFrame frame) =>
            frame.Market.AvailableAtUtc == Start.AddMinutes(1)
                ? [new("NIFTY22000CE", frame.Market.AvailableAtUtc,
                    CodeVersion, .8m, 75) { LotSize = 5 }] : [];

        public IReadOnlyList<HistoricalReplayPartialExit> EvaluatePartialExits(
            HistoricalReplayPortfolioFrame frame) =>
            frame.Market.AvailableAtUtc == Start.AddMinutes(3)
                ? [new("NIFTY22000CE", frame.Market.AvailableAtUtc,
                    30, "FirstReduction")]
                : frame.Market.AvailableAtUtc == Start.AddMinutes(5)
                    ? [new("NIFTY22000CE", frame.Market.AvailableAtUtc,
                        20, "SecondReduction")]
                    : [];
    }
}
