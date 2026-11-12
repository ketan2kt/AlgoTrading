using TradingSystem.Application.Execution;
using TradingSystem.Application.Regime;
using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class NiftyBreakoutRetestStrategyTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 12, 8, 30, 0, TimeSpan.Zero);
    private static readonly PriceActionStrategyOptions Options = new();

    [Theory]
    [InlineData(Direction.Buy, 60)]
    [InlineData(Direction.Sell, 60)]
    [InlineData(Direction.Buy, 300)]
    [InlineData(Direction.Sell, 300)]
    public void RetestCanQualifyAfterTheOriginalChaseIsRejected(Direction direction, int interval)
    {
        var context = Setup(direction, interval);
        var impulseBars = context.RecentCandles.SkipLast(3).ToArray();
        Assert.False(FirstEntryTimingPolicy.Evaluate(impulseBars, direction).Permitted);
        var strategy = new NiftyBreakoutRetestStrategy(Options);
        var signal = Assert.IsType<StrategySignal>(strategy.Evaluate(context));
        Assert.Equal(direction, signal.Direction);
        Assert.True(FirstEntryTimingPolicy.Evaluate(context.RecentCandles, direction).Permitted);
        var pivot = Assert.IsType<decimal>(signal.IndexConfirmationLevel);
        var limit = Assert.IsType<decimal>(signal.RetestChaseBoundary);
        Assert.True(NiftyBreakoutRetestStrategy.AllowsCurrentPrice(signal, context.CurrentPrice));
        Assert.False(NiftyBreakoutRetestStrategy.AllowsCurrentPrice(signal, pivot));
        Assert.False(NiftyBreakoutRetestStrategy.AllowsCurrentPrice(signal,
            limit + (direction == Direction.Buy ? .01m : -.01m)));
    }

    [Fact]
    public void RejectsAnUnfinishedFailedGappedOrFreshlyExtendedRetest()
    {
        var context = Setup(Direction.Buy, 60);
        var strategy = new NiftyBreakoutRetestStrategy(Options);
        Assert.Null(strategy.Evaluate(context with { ObservedAtUtc = context.ObservedAtUtc.AddSeconds(-1) }));
        Assert.Null(strategy.Evaluate(context with { DataTradingPermitted = false }));
        Assert.Null(strategy.Evaluate(context with { RelaxedNiftyEntries = false }));
        Assert.Null(strategy.Evaluate(context with
            { RecentCandles = context.RecentCandles.Where((_, i) => i != 16).ToArray() }));
        var bars = context.RecentCandles.ToArray();
        bars[^2] = bars[^2] with { Close = 21999.9m, Low = 21999.8m };
        Assert.Null(strategy.Evaluate(context with { RecentCandles = bars }));
        bars = context.RecentCandles.ToArray();
        bars[^1] = bars[^1] with { Close = 22001.6m, High = 22001.7m };
        Assert.Null(strategy.Evaluate(context with { RecentCandles = bars, CurrentPrice = bars[^1].Close }));
    }

    [Fact]
    public void PortfolioUsesRetestOnlyWhenItHasNoAcceptedSignalOrDirectionalConflict()
    {
        var context = Setup(Direction.Buy, 60);
        var retest = new NiftyBreakoutRetestStrategy(Options);
        Assert.Equal(NiftyBreakoutRetestStrategy.Id, new CompositeTradingStrategy([], retest).Evaluate(context)?.StrategyId);
        var opposing = new Stub("opposite", Direction.Sell);
        Assert.Null(new CompositeTradingStrategy([opposing], retest).Evaluate(context));
        Assert.Null(new CompositeTradingStrategy([new Stub("original", Direction.Buy), opposing],
            retest).Evaluate(context));
        Assert.Equal("first", new CompositeTradingStrategy([new Stub("first", Direction.Buy),
            new Stub("second", Direction.Buy)], retest).Evaluate(context)?.StrategyId);
    }

    [Fact]
    public void CompletedRetestCanPassTheNormalNiftyReplayEntryChain()
    {
        var setup = Setup(Direction.Buy, 300);
        var replayStart = Start.AddMinutes(-285); // 09:15 IST
        var bars = setup.RecentCandles.Select((b, index) =>
            {
                var shift = index < 16 ? (15 - index) * .3m : 0m;
                return new HistoricalReplayBar(replayStart.AddMinutes(index * 5), 300, b.Open-shift, b.High-shift,
                    b.Low-shift, b.Close-shift) { Symbol = "NIFTY" };
            }).ToArray();
        var all = bars.Concat(bars.Select(b => b with { Symbol = "NIFTY-FUT", Volume = 100 }))
            .Append(new HistoricalReplayBar(replayStart.AddDays(-1), 300, 21990m, 21992m, 21988m, 21990m)
                { Symbol = "NIFTY" }).ToArray();
        const string symbol = "NIFTY22000CE";
        var at = bars[^1].AvailableAtUtc;
        var retest = new NiftyBreakoutRetestStrategy(Options);
        var policy = new HistoricalNiftySignalPolicy(Guid.Empty, "NIFTY", "NIFTY-FUT",
            DateOnly.FromDateTime(Start.UtcDateTime), 21990m, new TimeOnly(9, 30), 15, 15, 300, 1,
            [new(Guid.Empty, symbol, InstrumentType.CallOption, new DateOnly(2026,11,17), 22000m, 65, .05m)],
            new NiftyOptionSelectionOptions(), new MarketRegimeEngine(new MarketRegimeOptions()),
            new CompositeTradingStrategy([], retest));
        var quote = new HistoricalReplayQuote(symbol, at.AddSeconds(-1), 100m) { BidPrice = 99.5m, OfferPrice = 100m };
        var frame = new HistoricalReplayPortfolioFrame(new(at, all, new Dictionary<string, HistoricalReplayQuote>
            { [symbol] = quote }), null, []);
        Assert.Equal(NiftyBreakoutRetestStrategy.Id, Assert.Single(policy.Evaluate(frame)).Strategy);
    }

    private static StrategyEvaluationContext Setup(Direction direction, int interval)
    {
        var bars = Enumerable.Range(0, 20).Select(i =>
            new StrategyPriceBar(Start.AddSeconds(i * interval), 21999m, 22000m, 21998m, 21999m)).ToArray();
        bars[^4] = bars[^4] with { Open = 21999.5m, High = 22004.6m, Low = 21999.4m, Close = 22004.2m };
        bars[^3] = bars[^3] with { Open = 22003m, High = 22003.1m, Low = 22000.8m, Close = 22000.9m };
        bars[^2] = bars[^2] with { Open = 22000.7m, High = 22000.8m, Low = 22000.1m, Close = 22000.4m };
        bars[^1] = bars[^1] with { Open = 22000.5m, High = 22001.1m, Low = 22000.4m, Close = 22001m };
        var buy = direction == Direction.Buy;
        if (!buy)
            bars = bars.Select(b => b with { Open = 44000m - b.Open, High = 44000m - b.Low,
                Low = 44000m - b.High, Close = 44000m - b.Close }).ToArray();
        return new(Guid.Empty, bars[^1].OpenTimeUtc.AddSeconds(interval), bars[^1].Close,
            22001m, 21998m, 1m, buy ? MarketRegime.StrongBullishTrend : MarketRegime.StrongBearishTrend,
            direction, .75m, true, true, null, 0)
        {
            RelaxedNiftyEntries = true, CandleIntervalSeconds = interval, RecentCandles = bars,
            Vwap = buy ? 21999m : 22001m, FastEma = 22000m,
            SlowEma = buy ? 21999m : 22001m, AtrPercent = 2m / bars[^1].Close * 100m
        };
    }

    private sealed class Stub(string id, Direction direction) : ITradingStrategy
    {
        public string StrategyId => id;
        public string Version => "test";
        public StrategySignal? Evaluate(StrategyEvaluationContext context) =>
            new(Guid.Empty, id, Version, context.InstrumentId, direction, SignalEntryType.Market,
                context.CurrentPrice, 22000m, 22005m, 2m, .8m, context.Regime,
                [], [], context.ObservedAtUtc, context.ObservedAtUtc.AddSeconds(30));
        public StrategyEvaluationResult EvaluateDetailed(StrategyEvaluationContext context) =>
            new(Evaluate(context), []);
    }
}
