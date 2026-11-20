using TradingSystem.Application.Execution;
using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class SetupBoundaryConsistencyTests
{
    [Theory]
    [InlineData("nifty", "ema-pullback-continuation", false)]
    [InlineData("sensex", "Sensex persistent directional thesis", false)]
    [InlineData("bank-nifty", "Bank index directional breakout", true)]
    public void SignalCandlePullbackDoesNotBypassNiftyOrSensexSetup(
        string market, string strategy, bool expected)
    {
        Assert.Equal(expected, Evaluate(market, strategy, Bars(), false).ShouldExit);
    }

    [Theory]
    [InlineData("nifty", "ema-pullback-continuation")]
    [InlineData("sensex", "Sensex persistent directional thesis")]
    public void MirroredPutPullbackRetainsSameBoundary(string market, string strategy) =>
        Assert.False(Evaluate(market, strategy, Bars(), true).ShouldExit);

    [Theory]
    [InlineData("nifty", "ema-pullback-continuation")]
    [InlineData("sensex", "Sensex persistent directional thesis")]
    public void ConfirmedBreakOfWholeSetupStillExits(string market, string strategy)
    {
        var bars = Bars();
        bars[^2] = bars[^2] with { Low = 25635m, Close = 25636m };
        bars[^1] = bars[^1] with { Open = 25636m, High = 25640m, Low = 25625m, Close = 25626m };
        Assert.True(Evaluate(market, strategy, bars, false, 25625m).ShouldExit);
        Assert.True(Evaluate(market, strategy, bars, true, 25625m).ShouldExit);
    }

    [Theory]
    [InlineData("nifty", "ema-pullback-continuation")]
    [InlineData("sensex", "Sensex persistent directional thesis")]
    public void MissingPreEntryBoundaryFailsClosed(string market, string strategy)
    {
        var bars = Bars().Where(b => b.OpenTimeUtc.Minute != 15).ToArray();
        Assert.False(Evaluate(market, strategy, bars, false).ShouldExit);
    }

    private static FailedSetupExitDecision Evaluate(string market, string strategy,
        StrategyPriceBar[] bars, bool mirrored, decimal spot = 25646.25m)
    {
        var at = new DateTimeOffset(2026, 11, 20, 12, 20, 13, TimeSpan.FromMinutes(330));
        if (mirrored)
        {
            bars = bars.Select(b => b with { Open = 52000m-b.Open, High = 52000m-b.Low,
                Low = 52000m-b.High, Close = 52000m-b.Close }).ToArray();
            spot = 52000m-spot;
        }
        return FailedSetupExitPolicy.EvaluateForMarket(market, strategy,
            mirrored ? Direction.Sell : Direction.Buy, 144.20m, 138.85m, 14.45m,
            147.40m, at.AddMinutes(-3).AddSeconds(-8), at, spot, at, bars, 60, 5);
    }

    // Native Nov20 index candles; option values are the recorded 12:17 entry.
    // No later recovery candle is supplied to the decision.
    private static StrategyPriceBar[] Bars() =>
    [
            new(new DateTimeOffset(2026, 11, 20, 11, 58, 0, TimeSpan.FromMinutes(330)), 25615.4m, 25623.6m, 25614.45m, 25620.35m),
            new(new DateTimeOffset(2026, 11, 20, 11, 59, 0, TimeSpan.FromMinutes(330)), 25621.05m, 25625.3m, 25616.3m, 25617.9m),
            new(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.FromMinutes(330)), 25618.8m, 25631.85m, 25618m, 25630m),
            new(new DateTimeOffset(2026, 11, 20, 12, 1, 0, TimeSpan.FromMinutes(330)), 25629.65m, 25636.35m, 25628.75m, 25629.25m),
            new(new DateTimeOffset(2026, 11, 20, 12, 2, 0, TimeSpan.FromMinutes(330)), 25627.9m, 25644.9m, 25627.9m, 25640.4m),
            new(new DateTimeOffset(2026, 11, 20, 12, 3, 0, TimeSpan.FromMinutes(330)), 25639.55m, 25644.75m, 25637.7m, 25641.45m),
            new(new DateTimeOffset(2026, 11, 20, 12, 4, 0, TimeSpan.FromMinutes(330)), 25641.5m, 25645.85m, 25639.45m, 25641.4m),
            new(new DateTimeOffset(2026, 11, 20, 12, 5, 0, TimeSpan.FromMinutes(330)), 25644.4m, 25644.4m, 25623.55m, 25632.8m),
            new(new DateTimeOffset(2026, 11, 20, 12, 6, 0, TimeSpan.FromMinutes(330)), 25632.6m, 25639.25m, 25629.05m, 25629.45m),
            new(new DateTimeOffset(2026, 11, 20, 12, 7, 0, TimeSpan.FromMinutes(330)), 25629.2m, 25635.65m, 25627.95m, 25635.05m),
            new(new DateTimeOffset(2026, 11, 20, 12, 8, 0, TimeSpan.FromMinutes(330)), 25634.8m, 25646.2m, 25634.6m, 25642.4m),
            new(new DateTimeOffset(2026, 11, 20, 12, 9, 0, TimeSpan.FromMinutes(330)), 25642.2m, 25651.2m, 25641.3m, 25649.2m),
            new(new DateTimeOffset(2026, 11, 20, 12, 10, 0, TimeSpan.FromMinutes(330)), 25653.1m, 25654.2m, 25647.4m, 25647.4m),
            new(new DateTimeOffset(2026, 11, 20, 12, 11, 0, TimeSpan.FromMinutes(330)), 25647.35m, 25654.25m, 25643.15m, 25643.9m),
            new(new DateTimeOffset(2026, 11, 20, 12, 12, 0, TimeSpan.FromMinutes(330)), 25643.7m, 25648.5m, 25637.6m, 25642.55m),
            new(new DateTimeOffset(2026, 11, 20, 12, 13, 0, TimeSpan.FromMinutes(330)), 25642.25m, 25653.9m, 25642.25m, 25649.65m),
            new(new DateTimeOffset(2026, 11, 20, 12, 14, 0, TimeSpan.FromMinutes(330)), 25650.5m, 25651.75m, 25644.7m, 25649.6m),
            new(new DateTimeOffset(2026, 11, 20, 12, 15, 0, TimeSpan.FromMinutes(330)), 25649.75m, 25655.35m, 25648.55m, 25654.7m),
            new(new DateTimeOffset(2026, 11, 20, 12, 16, 0, TimeSpan.FromMinutes(330)), 25654.25m, 25660.55m, 25654.25m, 25655.45m),
            new(new DateTimeOffset(2026, 11, 20, 12, 17, 0, TimeSpan.FromMinutes(330)), 25655.85m, 25659.4m, 25652.15m, 25653.8m),
            new(new DateTimeOffset(2026, 11, 20, 12, 18, 0, TimeSpan.FromMinutes(330)), 25653.45m, 25658.65m, 25651.15m, 25651.7m),
            new(new DateTimeOffset(2026, 11, 20, 12, 19, 0, TimeSpan.FromMinutes(330)), 25651.75m, 25655.95m, 25645.5m, 25647m)
    ];
}
