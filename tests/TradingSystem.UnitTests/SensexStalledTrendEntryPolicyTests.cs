using TradingSystem.Application.Execution;
using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class SensexStalledTrendEntryPolicyTests
{
    private static readonly DateTimeOffset Open = new(2026, 11, 12, 3, 45, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(Direction.Buy, 60)]
    [InlineData(Direction.Sell, 60)]
    [InlineData(Direction.Buy, 300)]
    [InlineData(Direction.Sell, 300)]
    public void FlatMatureThesisIsDeferredButOtherRoutesAndTheOpeningHourAreUnchanged(
        Direction direction, int intervalSeconds)
    {
        var bars = Flat(intervalSeconds);
        var firstDecision = bars[^1].OpenTimeUtc.AddSeconds(intervalSeconds);
        Assert.True(Evaluate(bars, direction, firstDecision.AddTicks(-1), intervalSeconds).Permitted);
        Assert.True(SensexStalledTrendEntryPolicy.Evaluate(false, [], direction, Open,
            firstDecision, intervalSeconds).Permitted);
        Assert.Equal(SensexStalledTrendEntryPolicy.Stalled,
            Evaluate(bars, direction, firstDecision, intervalSeconds).Rejection);
    }

    [Theory]
    [InlineData(Direction.Buy, 60, 5)]
    [InlineData(Direction.Sell, 60, 60)]
    [InlineData(Direction.Buy, 300, 60)]
    [InlineData(Direction.Sell, 300, 5)]
    public void AnAtrOfDirectionalProgressInEitherTimeWindowIsEnough(
        Direction direction, int intervalSeconds, int windowMinutes)
    {
        var bars = Flat(intervalSeconds);
        var anchor = bars.Length - 1 - windowMinutes * 60 / intervalSeconds;
        var value = direction == Direction.Buy ? 95m : 105m;
        bars[anchor] = bars[anchor] with
            { Close = value, High = Math.Max(102m, value), Low = Math.Min(98m, value) };
        // The current close remains inside the local swing. Only the chosen
        // time window provides an ATR of progress.
        Assert.True(Evaluate(bars, direction, bars[^1].OpenTimeUtc.AddSeconds(intervalSeconds),
            intervalSeconds).Permitted);
    }

    [Fact]
    public void ExactlyOneAtrOfProgressQualifiesWithoutBreakingTheSwing()
    {
        var bars = Flat(60);
        bars[^6] = bars[^6] with { Close = 98m };
        bars[^1] = bars[^1] with { Close = 102m };
        Assert.True(Evaluate(bars, Direction.Buy, bars[^1].OpenTimeUtc.AddMinutes(1)).Permitted);
    }

    [Theory]
    [InlineData(Direction.Buy)]
    [InlineData(Direction.Sell)]
    public void OnlyACompletedCloseBeyondTheSwingOverridesWeakProgress(Direction direction)
    {
        var bars = Flat(60);
        var at = bars[^1].OpenTimeUtc.AddMinutes(1);
        var boundary = direction == Direction.Buy ? 102m : 98m;
        var breakClose = direction == Direction.Buy ? 102.1m : 97.9m;
        bars[^1] = bars[^1] with { Close = boundary, High = 103m, Low = 97m };
        Assert.Equal(SensexStalledTrendEntryPolicy.Stalled, Evaluate(bars, direction, at).Rejection);
        var next = new StrategyPriceBar(at, 100m, 103.2m, 96.8m, breakClose);
        var extended = bars.Append(next).ToArray();
        Assert.Equal(SensexStalledTrendEntryPolicy.Stalled,
            Evaluate(extended, direction, at.AddSeconds(59)).Rejection);
        // The prior equality bar's wick is also part of the next swing.
        next = next with { Close = direction == Direction.Buy ? 103.1m : 96.9m };
        Assert.True(Evaluate(bars.Append(next).ToArray(), direction, at.AddMinutes(1)).Permitted);
    }

    [Fact]
    public void MatureThesisWithMissingStaleOrZeroRangeBarsCannotConfirm()
    {
        var bars = Flat(60);
        var at = bars[^1].OpenTimeUtc.AddMinutes(1);
        Assert.Equal(SensexStalledTrendEntryPolicy.Incomplete,
            Evaluate(bars.Where((_, index) => index != 47).ToArray(), Direction.Sell, at).Rejection);
        Assert.Equal(SensexStalledTrendEntryPolicy.Incomplete,
            Evaluate(bars, Direction.Sell, at.AddMinutes(1)).Rejection);
        Assert.Equal(SensexStalledTrendEntryPolicy.Incomplete, Evaluate(
            bars.Select(bar => bar with { High = 100m, Low = 100m }).ToArray(), Direction.Sell, at).Rejection);
    }

    [Fact]
    public void November12StalledPutIsDeferredAtTheRecordedProgressAndSwing()
    {
        const decimal close = 81821.97m;
        const decimal atr = 28.61357142857143m;
        var bars = Flat(60).Select(b => b with
            { Open = close, Close = close, Low = close - 1.49m, High = close - 1.49m + atr }).ToArray();
        // Spread the earlier closes inside their bar ranges so the latest
        // 15-bar true ranges stay exactly at the audited ATR.
        bars[0] = bars[0] with { Close = close + 21.80m };
        bars[^6] = bars[^6] with { Close = close + 17.30m };
        var decision = Evaluate(bars, Direction.Sell, bars[^1].OpenTimeUtc.AddMinutes(1));
        Assert.Equal(SensexStalledTrendEntryPolicy.Stalled, decision.Rejection);
        Assert.Contains("17.30", Assert.Single(decision.Reasons), StringComparison.Ordinal);
        Assert.Contains("21.80", Assert.Single(decision.Reasons), StringComparison.Ordinal);
    }

    private static StrategyPriceBar[] Flat(int intervalSeconds) =>
        Enumerable.Range(0, 3600 / intervalSeconds + 1).Select(index =>
            new StrategyPriceBar(Open.AddSeconds(index * intervalSeconds), 100m, 102m, 98m, 100m)).ToArray();

    private static SensexStalledTrendDecision Evaluate(StrategyPriceBar[] bars, Direction direction,
        DateTimeOffset at, int intervalSeconds = 60) =>
        SensexStalledTrendEntryPolicy.Evaluate(true, bars, direction, Open, at, intervalSeconds);
}
