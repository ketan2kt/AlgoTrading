using TradingSystem.Application.Execution;
using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class BankNiftyEntryStructurePolicyTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 12, 13, 4, 0, TimeSpan.FromMinutes(330));

    [Theory]
    [InlineData(Direction.Sell)]
    [InlineData(Direction.Buy)]
    public void ShortTermCountertrendBreakIntoAnIntactHourLevelWaits(Direction side)
    {
        var bars = Enumerable.Range(0, 91).Select(i => new StrategyPriceBar(Start.AddMinutes(i),
            i < 30 ? 90m : 104m, i < 30 ? 91m : 105m, i < 30 ? 89m : 103m, i < 30 ? 90m : 104m)).ToArray();
        bars[31] = bars[31] with { Low = 100m };
        bars[^1] = bars[^1] with { Close = 101m, Low = 101m };
        var now = bars[^1].OpenTimeUtc.AddMinutes(1);
        Assert.Equal(BankNiftyEntryStructurePolicy.OpposingLevel,
            BankNiftyEntryStructurePolicy.Evaluate(OnSide(bars, side), side, 2m, 60, now).Rejection);
        bars[^1] = bars[^1] with { Close = 99m, Low = 99m };
        Assert.True(BankNiftyEntryStructurePolicy.Evaluate(OnSide(bars, side), side, 2m, 60, now).Permitted);
    }

    [Fact]
    public void ShallowBreakAgainstTheHourIsRejectedEvenWhenEarlierPutNeverOpened()
    {
        // 14:52 sits inside the current five-minute bar; the shallow move
        // is not also a completed five-minute break.
        var bars = Enumerable.Range(0, 91).Select(i => new StrategyPriceBar(Start.AddMinutes(i + 17),
            100m, 101m, 99m, 100m)).ToArray();
        bars[30] = bars[30] with { Open = 104m, High = 105m, Low = 103m, Close = 104m };
        bars[^1] = bars[^1] with { High = 101.05m, Close = 101.05m };
        var now = bars[^1].OpenTimeUtc.AddMinutes(1);
        Assert.Equal(BankNiftyEntryStructurePolicy.MarginalBreak,
            BankNiftyEntryStructurePolicy.Evaluate(bars, Direction.Buy, 2m, 60, now).Rejection);
        bars[30] = bars[30] with { Open = 98m, High = 99m, Low = 97m, Close = 98m };
        Assert.True(BankNiftyEntryStructurePolicy.Evaluate(bars, Direction.Buy, 2m, 60, now).Permitted);
    }

    [Theory]
    [InlineData(Direction.Buy)]
    [InlineData(Direction.Sell)]
    public void FailedFlipNeedsAFiveMinuteBreakThatStartedAfterTheLoss(Direction side)
    {
        var start = new DateTimeOffset(2026, 11, 12, 14, 30, 0, Start.Offset);
        var bars = Enumerable.Range(0, 4).Select(i => new StrategyPriceBar(start.AddMinutes(5*i),
            100m, 102m, 99m, 101m)).ToArray();
        bars[0] = bars[0] with { High = 103m };
        var now = start.AddMinutes(20);
        bars[^1] = bars[^1] with { High = 102.5m, Close = 102.5m };
        Assert.Equal(BankNiftyEntryStructurePolicy.FailedFlip, Evaluate(start.AddMinutes(9)).Rejection);
        bars[^1] = bars[^1] with { High = 103.5m, Close = 103.5m };
        Assert.True(Evaluate(start.AddMinutes(9)).Permitted);
        Assert.Equal(BankNiftyEntryStructurePolicy.FailedFlip, Evaluate(start.AddMinutes(16)).Rejection);
        Assert.True(Evaluate(start).Permitted); // The existing 20-minute window expired.
        Assert.True(Evaluate(now.AddSeconds(1)).Permitted); // No future closure.

        BankNiftyEntryStructureDecision Evaluate(DateTimeOffset exit) =>
            BankNiftyEntryStructurePolicy.Evaluate(OnSide(bars, side), side, 2m, 300, now, exit);
    }

    [Theory]
    [InlineData(Direction.Buy)]
    [InlineData(Direction.Sell)]
    public void FailedSameSideRetryWaitsEvenAfterCooldownButACompactPostExitBaseCanRearm(Direction side)
    {
        var start = new DateTimeOffset(2026, 11, 13, 11, 20, 0, Start.Offset);
        var bars = Enumerable.Range(0, 29).Select(i => new StrategyPriceBar(start.AddMinutes(i),
            100m, 102m, 99m, 100m)).ToArray();
        var exit = start.AddMinutes(2).AddSeconds(35);
        var now = start.AddMinutes(29);
        bars[^1] = bars[^1] with { Close = 102.5m, High = 102.5m };
        Assert.True(BankNiftyEntryStructurePolicy.Evaluate(OnSide(bars, side), side, 1m, 60, now).Permitted);
        Assert.Equal(BankNiftyEntryStructurePolicy.FailedRetry, Evaluate().Rejection);

        for (var i = bars.Length - 5; i < bars.Length - 1; i++)
            bars[i] = bars[i] with { Open = 100.5m, Close = 100m, High = 100.6m, Low = 99.8m };
        bars[^1] = bars[^1] with { Open = 100m, Close = 101m, High = 101m, Low = 99.9m };
        Assert.True(Evaluate().Permitted);
        Assert.Equal(BankNiftyEntryStructurePolicy.FailedRetry,
            Evaluate(now.AddMinutes(-4)).Rejection); // Part of the base was formed before exit.
        Assert.True(Evaluate(now.AddSeconds(1)).Permitted); // A future exit is never used.

        BankNiftyEntryStructureDecision Evaluate(DateTimeOffset? at = null) =>
            BankNiftyEntryStructurePolicy.Evaluate(OnSide(bars, side), side, 1m, 60, now,
                sameSideFailedExit: at ?? exit);
    }

    [Theory]
    [InlineData(Direction.Buy)]
    [InlineData(Direction.Sell)]
    public void MultiBarImpulseAtItsExtremeWaitsButSmallerBreakRemainsEligible(Direction side)
    {
        // Sampled 13 Nov cash movement: 60924.40 -> 60799.70 in five minutes,
        // a roughly 4 ATR sell leg even though the final candle was under 2 ATR.
        var closes = new[] { 60970m, 60955m, 60942m, 60941m, 60933m,
            60924.4m, 60918m, 60880.15m, 60843m, 60845.6m, 60799.7m };
        var bars = closes.Select((close, i) => new StrategyPriceBar(Start.AddMinutes(i),
            i == 0 ? close : closes[i-1], i == 0 ? close : Math.Max(close, closes[i-1]),
            i == 0 ? close : Math.Min(close, closes[i-1]), close)).ToArray();
        var now = Start.AddMinutes(bars.Length);
        if (side == Direction.Buy)
            bars = bars.Select(b => b with { Open = 122000m-b.Open, High = 122000m-b.Low,
                Low = 122000m-b.High, Close = 122000m-b.Close }).ToArray();
        Assert.Equal(BankNiftyEntryStructurePolicy.ExtendedImpulse,
            BankNiftyEntryStructurePolicy.Evaluate(bars, side, 30m, 60, now).Rejection);
        Assert.True(BankNiftyEntryStructurePolicy.Evaluate(bars, side, 50m, 60, now).Permitted);
        var continuous = bars.Select((b, i) => i >= 5 ? b : b with
        {
            Close = bars[5].Close + (side == Direction.Buy ? -1 : 1) * (5-i) * 20m
        }).ToArray();
        Assert.True(BankNiftyEntryStructurePolicy.Evaluate(continuous, side, 30m, 60, now).Permitted);
    }

    private static StrategyPriceBar[] OnSide(StrategyPriceBar[] bars, Direction side) =>
        side == Direction.Sell && bars[0].Close >= 100m || side == Direction.Buy && bars[0].Close < 100m
            ? bars.Select(b => b with { Open = 200m-b.Open, High = 200m-b.Low, Low = 200m-b.High, Close = 200m-b.Close }).ToArray()
            : bars;
}
