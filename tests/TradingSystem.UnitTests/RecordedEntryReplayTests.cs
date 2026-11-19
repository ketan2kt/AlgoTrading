using TradingSystem.Application.Execution;

namespace TradingSystem.UnitTests;

public sealed class RecordedEntryReplayTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 18, 4, 0, 0, TimeSpan.Zero);
    private static HistoricalReplayCandidate Candidate => new("OPT", Start, "recorded", .8m, 30)
        { LotSize = 30, PlannedStopPrice = 90m, PlannedTargetPrice = 120m };
    private static HistoricalReplayQuote Quote(int seconds, decimal price, long size = 30) =>
        new("OPT", Start.AddSeconds(seconds), price)
        { BidPrice = price, OfferPrice = price + 1, BidQuantity = size, OfferQuantity = size,
            Source = "GrowwFeedSample", SourceAtUtc = Start.AddSeconds(seconds), DepthSourceAtUtc = Start.AddSeconds(seconds) };
    private static HistoricalReplayResult Run(IReadOnlyList<HistoricalReplayQuote> quotes, decimal entry = 100m) =>
        HistoricalPortfolioReplay.Run([], quotes, new NoEntries(),
            new(.1m, .2m, .01m, 0m, 5, 0) { RequireDisplayedFillSize = true, AllowModeledHistoricalCosts = true },
            Start, Start.AddMinutes(3), stopOnExecutionUncertainty: true,
            recordedEntry: new(Candidate, Start.AddSeconds(1), entry));

    [Fact]
    public void PreservesLedgerEntryAndFillsStopOnlyOnLaterObservedBid()
    {
        var result = Run([Quote(0, 101), Quote(2, 99), Quote(3, 89), Quote(4, 88)]);
        var trade = Assert.Single(result.Trades);
        Assert.Equal(100m, trade.EntryPrice); // Does not apply entry slippage again.
        Assert.Equal(Start.AddSeconds(1), trade.EntryAtUtc);
        Assert.Equal(88m, trade.ExitPrice);
        Assert.Equal(Start.AddSeconds(4), trade.ExitAtUtc);
        Assert.Equal("RecordedLedgerEntry", trade.EntryPriceSource);
        Assert.True(trade.Costs > 0);
        Assert.Equal(trade.GrossPnl - trade.Costs, trade.NetPnl);
    }

    [Fact]
    public void DoesNotInventExitWhenDisplayedSizeIsInsufficient()
    {
        var result = Run([Quote(2, 89), Quote(3, 88, 1), Quote(120, 87, 1)]);
        Assert.Empty(result.Trades);
        Assert.True(result.MissingExitQuoteCount > 0);
    }

    [Fact]
    public void RejectsInvalidOriginalRiskPlan() =>
        Assert.Throws<ArgumentException>(() => Run([], 89m));

    private sealed class NoEntries : IHistoricalReplayPortfolioPolicy
    {
        public string CodeVersion => "recorded-entry-test";
        public IReadOnlyList<HistoricalReplayCandidate> Evaluate(HistoricalReplayPortfolioFrame frame) =>
            throw new InvalidOperationException("An exit-only audit must not create new entries.");
    }
}
