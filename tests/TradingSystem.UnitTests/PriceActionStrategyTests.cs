using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class PriceActionStrategyTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 13, 5, 30, 0, TimeSpan.Zero);

    [Fact]
    public void OpeningRangeRetestSignalsWithoutHighRelativeVolume()
    {
        var strategy = new OpeningRangeRetestStrategy(new());
        var context = Context() with
        {
            CurrentPrice = 102.4m,
            RelativeVolume = 0.40m,
            RecentCandles =
            [
                Bar(100m, 101.8m, 99.8m, 101.5m),
                Bar(101.5m, 102.2m, 101.2m, 102m),
                Bar(102m, 102.1m, 100.9m, 101.2m),
                Bar(101.2m, 102.5m, 101.1m, 102.4m)
            ]
        };

        var result = strategy.EvaluateDetailed(context);

        Assert.NotNull(result.Signal);
        Assert.Equal(Direction.Buy, result.Signal.Direction);
        Assert.Equal("opening-range-retest", result.Signal.StrategyId);
        Assert.True(result.Signal.Confidence >= 0.55m);
    }

    [Fact]
    public void VwapPullbackSignalsOnConfirmedBearishContinuation()
    {
        var strategy = new VwapTrendPullbackStrategy(new());
        var context = Context() with
        {
            CurrentPrice = 98.5m,
            Vwap = 100m,
            FastEma = 99m,
            SlowEma = 100.5m,
            RegimeBias = Direction.Sell,
            RecentCandles =
            [
                Bar(101m, 101.2m, 100m, 100.2m),
                Bar(100.2m, 100.4m, 99.4m, 99.6m),
                Bar(99.5m, 100.1m, 99.3m, 99.7m),
                Bar(99.6m, 99.7m, 98.3m, 98.5m)
            ]
        };

        var signal = strategy.Evaluate(context);

        Assert.NotNull(signal);
        Assert.Equal(Direction.Sell, signal.Direction);
        Assert.True(signal.ProposedStopLoss > signal.ProposedEntry);
    }

    [Fact]
    public void CompositeSelectsHighestConfidenceQualifiedSetup()
    {
        var lower = new StubStrategy("lower", 0.60m);
        var higher = new StubStrategy("higher", 0.82m);
        var portfolio = new CompositeTradingStrategy([lower, higher]);

        var signal = portfolio.Evaluate(Context());

        Assert.NotNull(signal);
        Assert.Equal("higher", signal.StrategyId);
    }

    [Fact]
    public void ExplorationProfileDoesNotApplyPortfolioOrStrategyDailyCeilings()
    {
        var strategy = new OpeningRangeRetestStrategy(new()
        {
            EnforceDailyTradeLimits = false,
            MaximumTradesPerDay = 1,
            MaximumTradesPerStrategyPerDay = 1
        });
        var context = Context() with
        {
            CurrentPrice = 102.4m,
            TradesToday = 30,
            TradesByStrategyToday = new Dictionary<string, int>
            {
                ["opening-range-retest"] = 30
            },
            RecentCandles =
            [
                Bar(100m, 101.8m, 99.8m, 101.5m),
                Bar(101.5m, 102.2m, 101.2m, 102m),
                Bar(102m, 102.1m, 100.9m, 101.2m),
                Bar(101.2m, 102.5m, 101.1m, 102.4m)
            ]
        };

        Assert.NotNull(strategy.Evaluate(context));
    }

    [Fact]
    public void EmaPullbackSignalsAfterTrendResumes()
    {
        var context = Context() with
        {
            CurrentPrice = 102.5m,
            FastEma = 101.5m,
            SlowEma = 101m,
            RecentCandles =
            [
                Bar(100m, 101m, 99.8m, 100.8m), Bar(100.8m, 101.4m, 100.6m, 101.2m),
                Bar(101.2m, 101.8m, 101m, 101.6m), Bar(101.6m, 102m, 101.4m, 101.8m),
                Bar(101.8m, 102.1m, 101.5m, 101.9m), Bar(101.9m, 102.2m, 101.6m, 102m),
                Bar(102m, 102.1m, 101.3m, 101.6m), Bar(101.6m, 102.6m, 101.5m, 102.5m)
            ],
            MarketStructure = new(MarketStructureDirection.Bullish, 0.8m, 102.6m, 101m, 6)
        };

        var signal = new EmaPullbackContinuationStrategy(new()).Evaluate(context);

        Assert.NotNull(signal);
        Assert.Equal(Direction.Buy, signal.Direction);
    }

    [Fact]
    public void RangeRetestSignalsAfterConsolidationBreakout()
    {
        var context = Context() with
        {
            CurrentPrice = 102m,
            RecentCandles =
            [
                Bar(100m, 101m, 99.8m, 100.4m), Bar(100.4m, 101m, 100m, 100.7m),
                Bar(100.7m, 101m, 100.1m, 100.5m), Bar(100.5m, 100.9m, 100m, 100.6m),
                Bar(100.6m, 101m, 100.2m, 100.8m), Bar(100.8m, 101m, 100.3m, 100.9m),
                Bar(101.1m, 101.5m, 100.95m, 101.3m), Bar(101.3m, 102.1m, 101.2m, 102m)
            ]
        };

        Assert.NotNull(new RangeBreakoutRetestStrategy(new()).Evaluate(context));
    }

    [Fact]
    public void VwapRejectionSignalsAfterConfirmedReclaim()
    {
        var context = Context() with
        {
            CurrentPrice = 101.4m,
            Vwap = 100m,
            RecentCandles =
            [
                Bar(99.8m, 100.2m, 99.5m, 100.1m), Bar(100.1m, 100.5m, 99.8m, 100.3m),
                Bar(100.3m, 100.4m, 99.7m, 100.1m), Bar(100.1m, 101.5m, 100m, 101.4m)
            ],
            MarketStructure = new(MarketStructureDirection.Range, 0.7m, 101m, 99.5m, 3)
        };

        Assert.NotNull(new VwapRejectionReversalStrategy(new()).Evaluate(context));
    }

    [Fact]
    public void MomentumExpansionSignalsOnStructuralClose()
    {
        var context = Context() with
        {
            CurrentPrice = 103m,
            RecentCandles =
            [
                Bar(100m, 100.5m, 99.8m, 100.3m), Bar(100.3m, 100.8m, 100.1m, 100.6m),
                Bar(100.6m, 101m, 100.3m, 100.8m), Bar(100.8m, 101.2m, 100.5m, 101m),
                Bar(101m, 101.4m, 100.8m, 101.2m), Bar(101.2m, 103.2m, 101m, 103m)
            ],
            MarketStructure = new(MarketStructureDirection.Bullish, 0.8m, 103.2m, 100.5m, 5)
        };

        Assert.NotNull(new MomentumExpansionStrategy(new()).Evaluate(context));
    }

    private static StrategyEvaluationContext Context() => new(Guid.NewGuid(), Now, 102m,
        101m, 99m, 0.8m, MarketRegime.WeakBullishTrend, Direction.Buy, 0.65m,
        true, true, null, 0)
    {
        Vwap = 100m,
        FastEma = 101m,
        SlowEma = 100m,
        AtrPercent = 0.5m,
        RecentCandles =
        [
            Bar(100m, 101m, 99.8m, 100.8m), Bar(100.8m, 101.5m, 100.5m, 101.2m),
            Bar(101.2m, 101.4m, 100.8m, 101m), Bar(101m, 102.2m, 100.9m, 102m)
        ]
    };

    private static StrategyPriceBar Bar(decimal open, decimal high, decimal low, decimal close) =>
        new(Now, open, high, low, close);

    private sealed class StubStrategy(string id, decimal confidence) : ITradingStrategy
    {
        public string StrategyId => id;
        public string Version => "1";
        public StrategySignal? Evaluate(StrategyEvaluationContext context) => EvaluateDetailed(context).Signal;
        public StrategyEvaluationResult EvaluateDetailed(StrategyEvaluationContext context) =>
            new(new StrategySignal(Guid.NewGuid(), id, Version, context.InstrumentId, Direction.Buy,
                SignalEntryType.Market, 100m, 99m, 102m, 2m, confidence, context.Regime,
                [], [], context.ObservedAtUtc, context.ObservedAtUtc.AddMinutes(1)), []);
    }
}
