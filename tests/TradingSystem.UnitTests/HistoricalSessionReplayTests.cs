using TradingSystem.Application.Execution;

namespace TradingSystem.UnitTests;

public sealed class HistoricalSessionReplayTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 29, 4, 0, 0, TimeSpan.Zero);
    private static readonly HistoricalReplayParameters Parameters =
        new(0.10m, 0.15m, 0.0025m, 0.0025m, 5, 0);

    [Fact]
    public void EntryUsesOnlyQuoteReceivedAfterDecisionAndIncludesCosts()
    {
        var report = HistoricalSessionReplay.Run([
            Quote(0, 80m), Quote(1, 100m), Quote(2, 118m), Quote(3, 116m)
        ], [Candidate(0)], Parameters, Start.AddMinutes(10));

        var trade = Assert.Single(report.Trades);
        Assert.Equal(Start.AddMinutes(1), trade.EntryAtUtc);
        Assert.Equal(Start.AddMinutes(3), trade.ExitAtUtc);
        Assert.Equal("ObservedTarget", trade.ExitReason);
        Assert.True(trade.Costs > 0m);
        Assert.Equal(trade.GrossPnl - trade.Costs, trade.NetPnl);
    }

    [Fact]
    public void IdenticalSensexPathUsesBseCostsInsteadOfNseCosts()
    {
        HistoricalReplayQuote[] quotes = [
            Quote(0, 80m), Quote(1, 100m), Quote(2, 118m), Quote(3, 116m)
        ];
        HistoricalReplayCandidate[] candidates = [Candidate(0)];
        var nse = HistoricalSessionReplay.Run(quotes, candidates, Parameters,
            Start.AddMinutes(10), "NSE");
        var bse = HistoricalSessionReplay.Run(quotes, candidates, Parameters,
            Start.AddMinutes(10), "BSE");

        var nseTrade = Assert.Single(nse.Trades);
        var bseTrade = Assert.Single(bse.Trades);
        Assert.Equal(nseTrade.GrossPnl, bseTrade.GrossPnl);
        Assert.True(bseTrade.Costs < nseTrade.Costs);
        Assert.True(bseTrade.NetPnl > nseTrade.NetPnl);
    }

    [Fact]
    public void RecordedCandidateReplayUsesObservedOfferAndLaterBidWhenAvailable()
    {
        var report = HistoricalSessionReplay.Run([
            Quote(1, 100m) with { OfferPrice = 105m, BidPrice = 99m },
            Quote(2, 124m) with { OfferPrice = 125m, BidPrice = 123m },
            Quote(3, 119m) with { OfferPrice = 120m, BidPrice = 118m }
        ], [Candidate(0)], Parameters with
        {
            EntrySlippagePercent = 0m,
            ExitSlippagePercent = 0m
        }, Start.AddMinutes(10));

        var fill = Assert.Single(report.Trades);
        Assert.Equal(105m, fill.EntryPrice);
        Assert.Equal(118m, fill.ExitPrice);
        Assert.Equal("ObservedOffer", fill.EntryPriceSource);
        Assert.Equal("ObservedBid", fill.ExitPriceSource);
    }

    [Fact]
    public void AmbiguousSameTimeOptionQuotesAreRejected()
    {
        Assert.Throws<ArgumentException>(() => HistoricalSessionReplay.Run([
            Quote(1, 100m), Quote(1, 101m)
        ], [Candidate(0)], Parameters, Start.AddMinutes(10)));
    }

    [Fact]
    public void MissingOptionPathNeverCreatesProfitOrAnotherEntry()
    {
        var report = HistoricalSessionReplay.Run([
            Quote(1, 100m), Quote(4, 120m), Quote(5, 125m)
        ], [Candidate(0), Candidate(4)], Parameters, Start.AddMinutes(10));

        Assert.Empty(report.Trades);
        Assert.Equal(1, report.MissingExitQuoteCount);
        Assert.Equal(1, report.StaleQuoteCount);
        Assert.Equal(0m, report.NetPnl);
    }

    [Fact]
    public void SameDataAndParametersProduceIdenticalResearchResult()
    {
        HistoricalReplayQuote[] quotes = [Quote(1, 100m), Quote(2, 90m), Quote(3, 89m)];
        HistoricalReplayCandidate[] candidates = [Candidate(0)];
        var a = HistoricalSessionReplay.Run(quotes, candidates, Parameters, Start.AddMinutes(10));
        var b = HistoricalSessionReplay.Run(quotes.Reverse().ToArray(), candidates,
            Parameters, Start.AddMinutes(10));

        Assert.Equal(a.NetPnl, b.NetPnl);
        Assert.Equal(a.MaximumDrawdown, b.MaximumDrawdown);
        Assert.Equal(Assert.Single(a.Trades), Assert.Single(b.Trades));
        Assert.Equal("ObservedStop", Assert.Single(a.Trades).ExitReason);
    }

    [Fact]
    public void TriggerWithNoSubsequentFreshQuoteIsNotTreatedAsFilled()
    {
        var report = HistoricalSessionReplay.Run([
            Quote(1, 100m), Quote(2, 120m), Quote(4, 80m)
        ], [Candidate(0)], Parameters, Start.AddMinutes(10));

        Assert.Empty(report.Trades);
        Assert.Equal(1, report.MissingExitQuoteCount);
        Assert.True(report.StaleQuoteCount > 0);
    }

    [Fact]
    public void NoQuoteWithinNinetySecondsOfDecisionIsNotFilled()
    {
        var report = HistoricalSessionReplay.Run([Quote(2, 100m), Quote(3, 110m)],
            [Candidate(0)], Parameters, Start.AddMinutes(10));

        Assert.Empty(report.Trades);
        Assert.Equal(1, report.StaleQuoteCount);
    }

    [Fact]
    public void TimeExitDoesNotReuseEarlierPriceOrCarryIntoNextSession()
    {
        var report = HistoricalSessionReplay.Run([
            Quote(1, 100m), Quote(2, 101m), Quote(3, 102m), Quote(4, 103m),
            Quote(6, 105m)
        ], [Candidate(0)], Parameters, Start.AddMinutes(5));

        Assert.Empty(report.Trades);
        Assert.Equal(1, report.MissingExitQuoteCount);
    }

    [Fact]
    public void PurePolicyCanDiscoverUntradedEntryWithoutSeeingFutureBarsOrQuotes()
    {
        var policy = new TestEntryPolicy();
        var report = HistoricalSessionReplay.RunWithPolicy([
            new(Start, 60, 100m, 102m, 99m, 101m),
            new(Start.AddMinutes(1), 60, 101m, 103m, 100m, 102m)
        ], [Quote(0, 80m), Quote(2, 100m), Quote(3, 118m), Quote(4, 116m)], policy,
            Parameters, Start, Start.AddMinutes(5));

        Assert.Equal(2, policy.EvaluatedFrames);
        Assert.Equal(1, report.CandidateCount);
        Assert.Single(report.Trades);
        Assert.Equal(Start.AddMinutes(2), report.Trades[0].EntryAtUtc);
    }

    [Fact]
    public void PreviousSessionBarsWarmUpPolicyButCannotCreatePreviousSessionTrades()
    {
        var policy = new WarmupPolicy();
        var report = HistoricalSessionReplay.RunWithPolicy([
            new(Start.AddDays(-1), 60, 90m, 91m, 89m, 90m) { Symbol = "NIFTY" },
            new(Start, 60, 100m, 102m, 99m, 101m) { Symbol = "NIFTY" }
        ], [Quote(2, 100m), Quote(3, 118m), Quote(4, 116m)], policy,
            Parameters, Start, Start.AddMinutes(5));

        Assert.Equal(1, policy.Frames);
        Assert.Equal(2, policy.WarmBarsSeen);
        Assert.Single(report.Trades);
    }

    [Fact]
    public void SpotAndFuturesBarsCompletingTogetherCreateOneCompleteDecisionFrame()
    {
        var policy = new WarmupPolicy();
        var report = HistoricalSessionReplay.RunWithPolicy([
            new(Start, 60, 100m, 102m, 99m, 101m) { Symbol = "NIFTY" },
            new(Start, 60, 101m, 103m, 100m, 102m)
                { Symbol = "NIFTY-FUT", Volume = 1000 }
        ], [Quote(2, 100m), Quote(3, 118m), Quote(4, 116m)], policy,
            Parameters, Start, Start.AddMinutes(5));

        Assert.Equal(1, policy.Frames);
        Assert.Equal(2, policy.WarmBarsSeen);
        Assert.Equal(1, report.CandidateCount);
    }

    [Fact]
    public void QuoteStampedExactlyAtDecisionBarIsNotVisibleToPolicy()
    {
        var policy = new SameTimeQuotePolicy();
        HistoricalSessionReplay.RunWithPolicy([
            new(Start, 60, 100m, 102m, 99m, 101m) { Symbol = "NIFTY" },
            new(Start.AddMinutes(1), 60, 101m, 102m, 100m, 101m)
                { Symbol = "NIFTY" }
        ], [Quote(1, 120m)], policy, Parameters, Start,
            Start.AddMinutes(3));

        Assert.Equal([false, true], policy.QuoteWasVisible);
    }

    private sealed class SameTimeQuotePolicy : IHistoricalReplayEntryPolicy
    {
        public string CodeVersion => "same-time-quote-test";
        public List<bool> QuoteWasVisible { get; } = [];
        public IReadOnlyList<HistoricalReplayCandidate> Evaluate(
            HistoricalReplayFrame frame)
        {
            QuoteWasVisible.Add(frame.LatestQuotes.ContainsKey("NIFTY22000CE"));
            return [];
        }
    }

    private sealed class WarmupPolicy : IHistoricalReplayEntryPolicy
    {
        public string CodeVersion => "warmup-v1";
        public int Frames { get; private set; }
        public int WarmBarsSeen { get; private set; }
        public IReadOnlyList<HistoricalReplayCandidate> Evaluate(HistoricalReplayFrame frame)
        {
            Frames++;
            WarmBarsSeen = frame.CompletedBars.Count;
            return [new("NIFTY22000CE", frame.AvailableAtUtc, CodeVersion, 0.8m, 75)];
        }
    }

    private sealed class TestEntryPolicy : IHistoricalReplayEntryPolicy
    {
        public string CodeVersion => "test-policy-v1";
        public int EvaluatedFrames { get; private set; }

        public IReadOnlyList<HistoricalReplayCandidate> Evaluate(HistoricalReplayFrame frame)
        {
            EvaluatedFrames++;
            Assert.All(frame.CompletedBars, value =>
                Assert.True(value.AvailableAtUtc <= frame.AvailableAtUtc));
            Assert.All(frame.LatestQuotes.Values, value =>
                Assert.True(value.AvailableAtUtc <= frame.AvailableAtUtc));
            return frame.CompletedBars.Count == 1
                ? [new("NIFTY22000CE", frame.AvailableAtUtc, CodeVersion, 0.8m, 75)]
                : [];
        }
    }

    private static HistoricalReplayQuote Quote(int minute, decimal price) =>
        new("NIFTY22000CE", Start.AddMinutes(minute), price);

    private static HistoricalReplayCandidate Candidate(int minute) =>
        new("NIFTY22000CE", Start.AddMinutes(minute), "RecordedSignal", 0.7m, 75);
}
