using TradingSystem.Application.Execution;

namespace TradingSystem.UnitTests;

public sealed class HistoricalMultiMarketPortfolioPolicyTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 3,
        3, 45, 0, TimeSpan.Zero);
    private static readonly string[] Contracts =
        ["NIFTY22000CE", "SENSEX73000PE"];

    [Fact]
    public void PoliciesSeeOwnMarketStateAndSharedPortfolioOnOneClock()
    {
        var nifty = new MarketPolicy("NIFTY", "NIFTY22000CE");
        var sensex = new MarketPolicy("SENSEX", "SENSEX73000PE");
        var combined = new HistoricalMultiMarketPortfolioPolicy(
            new Dictionary<string, IHistoricalReplayPortfolioPolicy>
            {
                ["NIFTY"] = nifty, ["SENSEX"] = sensex
            });
        var bars = new[]
        {
            Bar("NIFTY", 0), Bar("SENSEX", 0),
            Bar("NIFTY", 2), Bar("SENSEX", 2)
        };
        var quotes = Contracts
            .SelectMany(symbol => new[]
            {
                new HistoricalReplayQuote(symbol, Start.AddMinutes(2), 100m),
                new HistoricalReplayQuote(symbol, Start.AddMinutes(3), 120m),
                new HistoricalReplayQuote(symbol, Start.AddMinutes(4), 115m)
            }).ToArray();
        var chains = new[]
        {
            new HistoricalOptionChainFrame(Start, Start,
                new DateOnly(2026, 11, 3),
                [new OptionOpenInterestStrike(22000m, 100m, 90m)])
            { UnderlyingSymbol = "NIFTY" },
            new HistoricalOptionChainFrame(Start, Start,
                new DateOnly(2026, 11, 3),
                [new OptionOpenInterestStrike(73000m, 300m, 280m)])
            { UnderlyingSymbol = "SENSEX" }
        };
        var result = HistoricalPortfolioReplay.Run(bars, quotes, combined,
            HistoricalReplayParameters.Default with
            {
                EntrySlippagePercent = 0m,
                ExitSlippagePercent = 0m,
                MaximumConcurrentPositions = 2
            }, Start, Start.AddMinutes(5), chains,
            contractExchanges: new Dictionary<string, string>
            {
                ["NIFTY22000CE"] = "NSE",
                ["SENSEX73000PE"] = "BSE"
            });

        Assert.Equal(2, result.CompletedPositions.Count);
        Assert.Equal(100m, nifty.SeenOi);
        Assert.Equal(300m, sensex.SeenOi);
        Assert.Equal(1, nifty.OwnOpenAtMinute3);
        Assert.Equal(1, sensex.OwnOpenAtMinute3);
        Assert.Equal(2, nifty.PortfolioOpenAtMinute3);
        Assert.Equal(2, sensex.PortfolioOpenAtMinute3);
        Assert.False(combined.ProductionPolicyParityVerified);
    }

    [Fact]
    public void ProviderNamedResearchFutureIsProjectedOnlyIntoItsOwnMarket()
    {
        var observer = new FutureObserver();
        var policy = new HistoricalMultiMarketPortfolioPolicy(new Dictionary<string, IHistoricalReplayPortfolioPolicy>
            { ["NIFTY"] = observer });
        policy.Evaluate(new(new(Start, [Bar("NSE-NIFTY-26Nov26-FUT", 0), Bar("BSE-SENSEX-26Nov26-FUT", 0)],
            new Dictionary<string, HistoricalReplayQuote>()), null, []));
        Assert.Equal("NSE-NIFTY-26Nov26-FUT", observer.SeenSymbol);
    }

    [Fact]
    public void UnchangedBarHistoryKeepsItsProjectionButNewCompletedBarsDoNot()
    {
        var observer = new FutureObserver();
        var policy = new HistoricalMultiMarketPortfolioPolicy(new Dictionary<string, IHistoricalReplayPortfolioPolicy>
            { ["NIFTY"] = observer });
        HistoricalReplayBar[] bars = [Bar("NIFTY", 0), Bar("SENSEX", 0)];
        var frame = new HistoricalReplayPortfolioFrame(new(Start.AddMinutes(1), bars,
            new Dictionary<string, HistoricalReplayQuote>()), null, []);
        policy.Evaluate(frame);
        var initial = observer.SeenBars;
        policy.Evaluate(frame with { Market = frame.Market with { AvailableAtUtc = Start.AddMinutes(1).AddSeconds(2) } });
        Assert.Same(initial, observer.SeenBars);
        policy.Evaluate(frame with { Market = frame.Market with { CompletedBars = new[] { Bar("NIFTY", 1), Bar("SENSEX", 1) } } });
        Assert.NotSame(initial, observer.SeenBars);
        Assert.Equal(Start.AddMinutes(1), Assert.Single(observer.SeenBars).OpenTimeUtc);
    }

    private sealed class FutureObserver : IHistoricalReplayPortfolioPolicy
    {
        public string CodeVersion => "future-projection-test";
        public string? SeenSymbol { get; private set; }
        public IReadOnlyList<HistoricalReplayBar> SeenBars { get; private set; } = [];
        public IReadOnlyList<HistoricalReplayCandidate> Evaluate(HistoricalReplayPortfolioFrame frame)
        {
            SeenBars = frame.Market.CompletedBars;
            SeenSymbol = Assert.Single(frame.Market.CompletedBars).Symbol;
            return [];
        }
    }

    [Fact]
    public void MarketPolicyCannotEmitAnotherMarketsContract()
    {
        var combined = new HistoricalMultiMarketPortfolioPolicy(
            new Dictionary<string, IHistoricalReplayPortfolioPolicy>
            {
                ["NIFTY"] = new MarketPolicy("NIFTY", "SENSEX73000PE")
            });
        Assert.Throws<InvalidOperationException>(() =>
            HistoricalPortfolioReplay.Run([Bar("NIFTY", 0)], [], combined,
                HistoricalReplayParameters.Default, Start,
                Start.AddMinutes(3)));
    }

    private static HistoricalReplayBar Bar(string symbol, int minute) =>
        new(Start.AddMinutes(minute), 60, 100m, 101m, 99m, 100m)
        { Symbol = symbol };

    private sealed class MarketPolicy(string market, string contract)
        : IHistoricalReplayPortfolioPolicy
    {
        public string CodeVersion => market + "-test-v1";
        public decimal? SeenOi { get; private set; }
        public int? OwnOpenAtMinute3 { get; private set; }
        public int? PortfolioOpenAtMinute3 { get; private set; }
        public IReadOnlyList<HistoricalReplayCandidate> Evaluate(
            HistoricalReplayPortfolioFrame frame)
        {
            SeenOi = frame.LatestOptionChain?.Strikes.Single().CallOpenInterest;
            if (frame.Market.AvailableAtUtc == Start.AddMinutes(3))
            {
                OwnOpenAtMinute3 = frame.OpenPositions.Count;
                PortfolioOpenAtMinute3 = frame.PortfolioOpenPositions.Count;
            }
            return frame.Market.AvailableAtUtc == Start.AddMinutes(1)
                ? [new(contract, frame.Market.AvailableAtUtc,
                    CodeVersion, .8m, 75)] : [];
        }
    }
}
