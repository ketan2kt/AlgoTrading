using TradingSystem.Application.Execution;

namespace TradingSystem.UnitTests;

public sealed class HistoricalSharedPortfolioComparisonTests
{
    private static readonly HistoricalReplayParameters Parameters =
        HistoricalReplayParameters.Default with
        {
            EntrySlippagePercent = 0m,
            ExitSlippagePercent = 0m,
            MaximumConcurrentPositions = 2
        };

    [Fact]
    public void ComparesMarketsOnOneClockAndExcludesOldIncompleteDate()
    {
        var old = new DateOnly(2026, 10, 26);
        var current = new DateOnly(2026, 11, 3);
        var source = new[]
        {
            Session(old, "NIFTY", "NIFTY22000CE", "NSE") with
            { ExecutableQuoteSidesComplete = false },
            Session(old, "SENSEX", "SENSEX73000PE", "BSE"),
            Session(current, "NIFTY", "NIFTY22000CE", "NSE"),
            Session(current, "SENSEX", "SENSEX73000PE", "BSE")
        };
        var result = HistoricalSharedPortfolioComparison.RunEligible(source,
            ["NIFTY", "SENSEX"],
            (_, market) => new OneEntryPolicy(market, true),
            (_, market) => new OneEntryPolicy(market, false),
            Parameters, Parameters);

        var day = Assert.Single(result.Days);
        Assert.Equal(current, day.Date);
        Assert.Equal(2, day.Baseline.CompletedPositions.Count);
        Assert.Empty(day.Proposal.CompletedPositions);
        Assert.Equal(2, result.BaselineTrades);
        Assert.Equal(0, result.ProposalTrades);
        Assert.Equal(day.Baseline.TotalCosts, result.BaselineCosts);
        Assert.Equal(day.Baseline.WinRate, result.BaselineWinRate);
        Assert.Equal(result.BaselineNet / 2m, result.BaselineExpectancy);
        Assert.Single(result.RegimeComparisons);
        Assert.Equal(2, result.RegimeComparisons[0].BaselineTrades);
        Assert.Equal(result.BaselineNet * -1m, day.NetDifference);
        Assert.Equal(old, Assert.Single(result.Cohort.ExcludedDates).Date);
        Assert.Equal("InsufficientCoverage", result.EvidenceStatus);
        Assert.Contains(result.EvidenceGaps, value =>
            value.Contains("market-scoped admission"));
        Assert.Equal(result.DataFingerprint,
            HistoricalSharedPortfolioComparison.RunEligible(source.Reverse().ToArray(),
                ["NIFTY", "SENSEX"],
                (_, market) => new OneEntryPolicy(market, true),
                (_, market) => new OneEntryPolicy(market, false),
                Parameters, Parameters).DataFingerprint);
    }

    [Fact]
    public void RejectsPolicyConfigurationThatChangesWhileReplaying()
    {
        var date = new DateOnly(2026, 11, 3);
        var session = Session(date, "NIFTY", "NIFTY22000CE", "NSE");
        Assert.Throws<InvalidOperationException>(() =>
            HistoricalSharedPortfolioComparison.RunEligible([session], ["NIFTY"],
                (_, _) => new MutatingPolicy(),
                (_, _) => new OneEntryPolicy("NIFTY", false),
                Parameters, Parameters));
    }

    [Fact]
    public void ThreeMarketsShareAClockButKeepTheirOwnExchangeAndRiskCapacity()
    {
        var date = new DateOnly(2026, 11, 3);
        var sessions = new[]
        {
            Session(date, "NIFTY", "NIFTY22000CE", "NSE"),
            Session(date, "SENSEX", "SENSEX73000PE", "BSE"),
            Session(date, "BANKNIFTY", "BANKNIFTY54000CE", "NSE")
        };
        var parameters = Parameters with { MaximumConcurrentPositions = 3 };
        var result = HistoricalSharedPortfolioComparison.RunEligible(sessions,
            ["NIFTY", "SENSEX", "BANKNIFTY"],
            (_, market) => new OneEntryPolicy(market, true),
            (_, market) => new OneEntryPolicy(market, false),
            parameters, parameters);

        var day = Assert.Single(result.Days);
        Assert.Equal(3, day.Baseline.CompletedPositions.Count);
        Assert.Equal(3, result.BaselineTrades);
        Assert.Equal(3, day.Baseline.PeakConcurrentPositions);
        Assert.Equal(22500m, day.Baseline.PeakEntryCapitalExposure);
        Assert.Contains(day.Baseline.Trades, value =>
            value.Symbol.StartsWith("SENSEX", StringComparison.Ordinal) &&
            value.ExchangeCode == "BSE");
        Assert.Equal(2, day.Baseline.Trades.Count(value =>
            value.ExchangeCode == "NSE"));
        Assert.Equal("InsufficientCoverage", result.EvidenceStatus);
    }

    [Fact]
    public void MarketPolicyCannotSeeSameTimestampOptionQuoteAtBarDecision()
    {
        var date = new DateOnly(2026, 11, 3);
        var session = Session(date, "NIFTY", "NIFTY22000CE", "NSE");
        var quoteAt = session.Quotes[0].AvailableAtUtc;
        var probe = new SameTimeQuoteProbePolicy(quoteAt);

        HistoricalSharedPortfolioComparison.RunEligible([session], ["NIFTY"],
            (_, _) => probe,
            (_, _) => new OneEntryPolicy("NIFTY", false),
            Parameters, Parameters);

        Assert.True(probe.Checked);
    }

    [Fact]
    public void HeldOutMetricsUseLaterWholeDatesOnly()
    {
        var first = Session(new DateOnly(2026, 11, 2), "NIFTY",
            "NIFTY22000CE", "NSE");
        var second = Session(new DateOnly(2026, 11, 3), "NIFTY",
            "NIFTY22000CE", "NSE");
        var result = HistoricalSharedPortfolioComparison.RunEligible(
            [second, first], ["NIFTY"],
            (_, market) => new OneEntryPolicy(market, true),
            (_, market) => new OneEntryPolicy(market, false),
            Parameters, Parameters);

        Assert.Equal(1, result.TrainingDays);
        Assert.Equal(1, result.ValidationDays);
        Assert.Equal(result.Days[0].Baseline.NetPnl,
            result.TrainingBaseline.Net);
        Assert.Equal(result.Days[1].Baseline.NetPnl,
            result.ValidationBaseline.Net);
        Assert.Equal(1, result.ValidationBaseline.Trades);
        Assert.Equal(0, result.ValidationProposal.Trades);
    }

    private static HistoricalReplaySessionData Session(DateOnly date,
        string market, string contract, string exchange)
    {
        var start = new DateTimeOffset(date.ToDateTime(new TimeOnly(9, 15)),
            TimeSpan.FromHours(5.5)).ToUniversalTime();
        var bars = Enumerable.Range(0, 375).Select(minute =>
            new HistoricalReplayBar(start.AddMinutes(minute), 60,
                100m, 101m, 99m, 100m) { Symbol = market }).ToArray();
        var quotes = new[]
        {
            Quote(contract, start.AddMinutes(2), 100m),
            Quote(contract, start.AddMinutes(3), 120m),
            Quote(contract, start.AddMinutes(4), 115m)
        };
        return new(date, market, bars, quotes, start,
            start.AddMinutes(375))
        {
            ExchangeCode = exchange,
            RequiredInputsComplete = true,
            EligibleOptionUniverseComplete = true,
            StrategyInputsComplete = true,
            ExecutableQuoteSidesComplete = true,
            HistoricalChargesVerified = true,
            PolicyInputManifest = "frozen-test-input-v1",
            OptionChains = [new HistoricalOptionChainFrame(start, start,
                date.AddDays(7),
                [new OptionOpenInterestStrike(100m, 1000m, 900m)])
                { UnderlyingSymbol = market }]
        };
    }

    private static HistoricalReplayQuote Quote(string contract,
        DateTimeOffset time, decimal price) => new(contract, time, price)
    { BidPrice = price, OfferPrice = price };

    private sealed class OneEntryPolicy(string market, bool enter)
        : IHistoricalReplayPortfolioPolicy
    {
        public string CodeVersion => "one-entry-test-v1";
        public string ConfigurationManifest => market + "|enter=" + enter;
        public IReadOnlyList<HistoricalReplayCandidate> Evaluate(
            HistoricalReplayPortfolioFrame frame) => enter &&
            frame.Market.CompletedBars.Count > 0 &&
            frame.Market.AvailableAtUtc ==
            frame.Market.CompletedBars[0].AvailableAtUtc
                ? [new(market switch
                    {
                        "NIFTY" => "NIFTY22000CE",
                        "SENSEX" => "SENSEX73000PE",
                        "BANKNIFTY" => "BANKNIFTY54000CE",
                        _ => throw new InvalidOperationException()
                    }, frame.Market.AvailableAtUtc,
                    CodeVersion, .8m, 75)] : [];
    }

    private sealed class MutatingPolicy : IHistoricalReplayPortfolioPolicy
    {
        private int evaluated;
        public string CodeVersion => "mutating-policy-v1";
        public string ConfigurationManifest => $"evaluated={evaluated}";
        public IReadOnlyList<HistoricalReplayCandidate> Evaluate(
            HistoricalReplayPortfolioFrame _) { evaluated++; return []; }
    }

    private sealed class SameTimeQuoteProbePolicy(DateTimeOffset quoteAt)
        : IHistoricalReplayPortfolioPolicy
    {
        public bool Checked { get; private set; }
        public string CodeVersion => "same-time-quote-probe-v1";
        public string ConfigurationManifest => "same-time-quote-probe-v1";
        public IReadOnlyList<HistoricalReplayCandidate> Evaluate(
            HistoricalReplayPortfolioFrame frame)
        {
            if (frame.Market.AvailableAtUtc == quoteAt)
            {
                Checked = true;
                Assert.DoesNotContain("NIFTY22000CE", frame.Market.LatestQuotes.Keys);
            }
            return [];
        }
    }
}
