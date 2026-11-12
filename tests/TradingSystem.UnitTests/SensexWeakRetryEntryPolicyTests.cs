using TradingSystem.Application.Execution;
using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class SensexWeakRetryEntryPolicyTests
{
    private static readonly DateTimeOffset Open = new(2026, 11, 12, 3, 45, 0, TimeSpan.Zero);
    private static readonly MarketStateAssessmentV2 Weak = new(IntradayMarketStateV2.BearTrend,
        Direction.Sell, .61m, .1236m, .43m, .14m, .05m, .79m, 4.42m, []);

    [Theory]
    [InlineData(Direction.Buy, 60)]
    [InlineData(Direction.Sell, 60)]
    [InlineData(Direction.Buy, 300)]
    [InlineData(Direction.Sell, 300)]
    public void WeakRetryNeedsBreakRetestAndSeparateNonChasingResumption(Direction side, int interval)
    {
        var bars = Setup(side, interval);
        var exit = Open.AddSeconds(14 * interval - 1);
        SensexWeakRetryDecision At(int count) => Evaluate(bars[..count], side, interval, exit);

        Assert.False(At(30).Permitted); // The retry is close to the level, past the cooldown.
        Assert.False(At(31).Permitted); // First close through it does not reset the watch.
        Assert.False(At(32).Permitted); // The retest alone is not a resumption.
        Assert.True(At(33).Permitted);
        Assert.False(At(34).Permitted); // Price is now too far past the frozen level.
        Assert.False(At(35).Permitted); // A close back through the level invalidates the retest.
    }

    [Fact]
    public void FirstEntryWinningExitDifferentSessionAndCleanTrendAreUnchanged()
    {
        var bars = Setup(Direction.Sell, 60)[..30];
        var exit = Open.AddMinutes(14).AddSeconds(-1);
        Assert.True(Evaluate(bars, Direction.Sell, 60, null).Permitted);
        Assert.True(Evaluate(bars, Direction.Sell, 60, exit, pnl: 5m).Permitted);
        Assert.True(Evaluate(bars, Direction.Sell, 60, exit, status: "StopLossHit").Permitted);
        Assert.True(Evaluate(bars, Direction.Sell, 60, exit.AddDays(-1)).Permitted);
        Assert.True(Evaluate(bars, Direction.Sell, 60, exit,
            state: Weak with { DirectionalEfficiency = .30m }).Permitted);
    }

    [Fact]
    public void IncompleteOrFutureBarsCannotProveAReset()
    {
        var bars = Setup(Direction.Sell, 60);
        var exit = Open.AddMinutes(14).AddSeconds(-1);
        Assert.False(Evaluate(bars[..33].Where((_, i) => i != 30).ToArray(), Direction.Sell, 60, exit).Permitted);
        Assert.False(Evaluate(bars, Direction.Sell, 60, exit, asOf: Open.AddMinutes(32)).Permitted);
    }

    private static SensexWeakRetryDecision Evaluate(StrategyPriceBar[] bars, Direction side, int interval,
        DateTimeOffset? exit, decimal pnl = -3391.33m, string status = "FailedSetupExit",
        MarketStateAssessmentV2? state = null, DateTimeOffset? asOf = null) =>
        SensexWeakRetryEntryPolicy.Evaluate(bars, side, Open, asOf ?? bars[^1].OpenTimeUtc.AddSeconds(interval),
            interval, exit, status, pnl, state ?? Weak with { Bias = side });

    private static StrategyPriceBar[] Setup(Direction side, int interval)
    {
        StrategyPriceBar Bar(int i, decimal open, decimal high, decimal low, decimal close) => side == Direction.Buy
            ? new(Open.AddSeconds(i * interval), open, high, low, close)
            : new(Open.AddSeconds(i * interval), 200m - open, 200m - low, 200m - high, 200m - close);
        return [.. Enumerable.Range(0, 29).Select(i => Bar(i, 99.2m, 100m, 98.8m, i % 2 == 0 ? 99m : 99.5m)),
            Bar(29, 99.1m, 100m, 98.9m, 99.7m),
            Bar(30, 99.7m, 100.3m, 99.7m, 100.2m),
            Bar(31, 100.2m, 100.26m, 100.02m, 100.06m),
            Bar(32, 100.12m, 100.6m, 100.12m, 100.47m),
            Bar(33, 100.47m, 101.1m, 100.42m, 101.05m),
            Bar(34, 101.05m, 101.05m, 99.7m, 99.85m)];
    }
}
