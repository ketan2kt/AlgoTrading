using TradingSystem.Application.Execution;
using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class NormalMarketFailedEntryExitTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 9, 2, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Entry = Now.AddMinutes(-3).AddSeconds(3);

    [Theory]
    [InlineData("opening-range-breakout")]
    [InlineData("opening-range-retest")]
    [InlineData("vwap-trend-pullback")]
    [InlineData("ema-pullback-continuation")]
    [InlineData("intraday-range-breakout-retest")]
    [InlineData("vwap-rejection-reversal")]
    [InlineData("momentum-expansion")]
    public void AllOrdinaryNiftyPathsAndTheirExplorationVariantsAreEligible(string strategy)
    {
        Assert.True(FailedSetupExitPolicy.AllowsEntryReclaim("nifty", strategy));
        Assert.True(FailedSetupExitPolicy.AllowsEntryReclaim("nifty", "exploration-" + strategy));
        Assert.False(FailedSetupExitPolicy.AllowsEntryReclaim("nifty", "selective-hedge-" + strategy));
    }

    [Theory]
    [InlineData("nifty", Direction.Buy)]
    [InlineData("nifty", Direction.Sell)]
    [InlineData("bank-nifty", Direction.Buy)]
    [InlineData("bank-nifty", Direction.Sell)]
    public void ScaledCallAndPutReclaimsExitButNormalPullbacksAndPriorWinnersDoNot(string market, Direction direction)
    {
        // Synthetic scaled variants of the recorded structure, not Nifty/Bank Nifty trade history.
        var (bars, spot) = Structure(market, direction);
        var strategy = market == "nifty" ? "momentum-expansion" : "Bank index directional breakout";
        FailedSetupExitDecision Evaluate(decimal option = 96m, decimal peak = 100.5m,
            DateTimeOffset? quoteAt = null, DateTimeOffset? enteredAt = null,
            IReadOnlyList<StrategyPriceBar>? candles = null, decimal? currentSpot = null) =>
            FailedSetupExitPolicy.EvaluateForMarket(market, strategy, direction,
                100m, option, 10m, peak, enteredAt ?? Entry, Now, currentSpot ?? spot,
                quoteAt ?? Now, candles ?? bars, 60, 5);
        Assert.False(FailedSetupExitPolicy.Evaluate(direction, 100m, 96m, 10m,
            100.5m, Entry, Now, spot, bars).ShouldExit);
        Assert.True(Evaluate().ShouldExit);
        Assert.False(Evaluate(option: 99m).ShouldExit);
        Assert.False(Evaluate(peak: 105m).ShouldExit);
        Assert.False(Evaluate(quoteAt: Now.AddSeconds(-6)).ShouldExit);
        Assert.False(Evaluate(quoteAt: Now.AddSeconds(1)).ShouldExit);
        Assert.False(Evaluate(enteredAt: Now.AddMinutes(-1)).ShouldExit);
        Assert.False(Evaluate(currentSpot: bars[8].Close).ShouldExit);
        var pullback = bars.ToArray();
        pullback[^2] = pullback[^2] with { Close = bars[8].Close };
        Assert.False(Evaluate(candles: pullback).ShouldExit);
        var incomplete = bars.ToArray();
        incomplete[^1] = incomplete[^1] with { OpenTimeUtc = Now };
        Assert.False(Evaluate(candles: incomplete).ShouldExit);
        var gap = bars.Where((_, index) => index != 9).ToArray();
        Assert.False(Evaluate(candles: gap).ShouldExit);
    }

    [Theory]
    [InlineData("nifty", Direction.Buy)]
    [InlineData("nifty", Direction.Sell)]
    [InlineData("bank-nifty", Direction.Buy)]
    [InlineData("bank-nifty", Direction.Sell)]
    public void ReplayWiresSameExitAndPreservesStopsAndFreshness(string market, Direction direction)
    {
        var (bars, spot) = Structure(market, direction);
        var underlying = market == "nifty" ? "NIFTY" : "BANKNIFTY";
        var symbol = underlying + (direction == Direction.Buy ? "25000CE" : "25000PE");
        var strategy = market == "nifty" ? "momentum-expansion" : "Bank index directional breakout";
        var position = new HistoricalReplayOpenPosition(symbol, strategy, Entry, 100m, 96m,
            150, 90m, 110m) { OriginalQuantity = 150, InitialStopPrice = 90m, InitialTargetPrice = 110m,
                LotSize = 30, UnderlyingDirection = direction,
                DecisionAtUtc = Entry.AddMinutes(-1), HighestDecisionPrice = 100.5m };
        var replayBars = bars.Select(b => new HistoricalReplayBar(b.OpenTimeUtc, 60,
            b.Open, b.High, b.Low, b.Close) { Symbol = underlying }).ToArray();
        var quotes = new Dictionary<string, HistoricalReplayQuote>
            { [symbol] = new(symbol, Now, 96m), [underlying] = new(underlying, Now, spot) };
        var frame = new HistoricalReplayPortfolioFrame(new(Now, replayBars, quotes), position, [])
            { OpenPositions = [position] };
        IHistoricalReplayPortfolioPolicy policy = market == "nifty"
            ? new HistoricalNiftyPositionPolicy(new NoEntries(),
                new(new TimeOnly(15, 15), true, true, true, true, .6m, true, .55m, 3, true, .8m, 5, 5, 60))
            : new HistoricalMarketOptionPositionPolicy(new NoEntries(), market, underlying,
                new(new TimeOnly(15, 15), true, true, true, .55m, 3, true, .8m, 5, 5, 60),
                [new(Guid.Empty, symbol, direction == Direction.Buy ? InstrumentType.CallOption : InstrumentType.PutOption,
                    new DateOnly(2026, 11, 24), 25000m, 30, .05m)]);
        Assert.Equal(market == "nifty" ? "FailedSetup" : "FailedSetupExit",
            Assert.Single(policy.EvaluateExits(frame)).Reason);
        var stopped = position with { StopPrice = 97m };
        Assert.Equal(market == "nifty" ? "StopLoss" : "StopLossHit", Assert.Single(policy.EvaluateExits(
            frame with { OpenPosition = stopped, OpenPositions = [stopped] })).Reason);
        var winner = position with { HighestDecisionPrice = 105m };
        Assert.Empty(policy.EvaluateExits(frame with { OpenPosition = winner, OpenPositions = [winner] }));
        quotes[underlying] = new(underlying, Now.AddSeconds(-6), spot);
        Assert.Empty(policy.EvaluateExits(frame));
    }

    private static (StrategyPriceBar[] Bars, decimal Spot) Structure(string market, Direction direction)
    {
        var scale = market == "nifty" ? .3m : .7m;
        var origin = market == "nifty" ? 24000m : 55000m;
        decimal Transform(decimal value) => origin + (value - 79000m) * scale *
            (direction == Direction.Sell ? 1m : -1m);
        var bars = SensexFailedThesisExitTests.RecordedBars().Select(b => b with
        {
            Open = Transform(b.Open), Close = Transform(b.Close),
            High = Transform(direction == Direction.Sell ? b.High : b.Low),
            Low = Transform(direction == Direction.Sell ? b.Low : b.High)
        }).ToArray();
        return (bars, Transform(79274.49m));
    }

    private sealed class NoEntries : IHistoricalReplayPortfolioPolicy
    {
        public string CodeVersion => "fixture-only";
        public IReadOnlyList<HistoricalReplayCandidate> Evaluate(HistoricalReplayPortfolioFrame frame) => [];
    }
}
