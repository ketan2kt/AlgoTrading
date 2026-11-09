using TradingSystem.Application.Execution;
using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class FirstEntryTimingPolicyTests
{
    [Fact]
    public void RejectsEntryAfterExtendedBreakout()
    {
        var bars = Bars(Enumerable.Range(0, 15).Select(i => 100m + i * .1m).Append(104m).ToArray());

        var result = FirstEntryTimingPolicy.Evaluate(bars, Direction.Buy);

        Assert.False(result.Permitted);
        Assert.True(result.AtrExtension > .45m);
    }

    [Fact]
    public void AllowsEntryNearTriggerWithoutOversizedImpulse()
    {
        var closes = Enumerable.Range(0, 16).Select(i => 100m + i * .10m).ToArray();
        var bars = Bars(closes);

        var result = FirstEntryTimingPolicy.Evaluate(bars, Direction.Buy);

        Assert.True(result.Permitted);
    }

    [Theory]
    [InlineData(Direction.Buy)]
    [InlineData(Direction.Sell)]
    public void AllowsModestExtensionAbovePreviousLimit(Direction direction)
    {
        var bars = FlatBarsWithLatest(100m, 102.4m, 100m, 102.2m, direction);

        var result = FirstEntryTimingPolicy.Evaluate(bars, direction);

        Assert.InRange(result.AtrExtension, .45m, .65m);
        Assert.True(result.Permitted);
    }

    [Theory]
    [InlineData(Direction.Buy)]
    [InlineData(Direction.Sell)]
    public void AllowsModestImpulseAbovePreviousLimit(Direction direction)
    {
        var bars = FlatBarsWithLatest(98.8m, 101.4m, 98.6m, 101.2m, direction);

        var result = FirstEntryTimingPolicy.Evaluate(bars, direction);

        Assert.InRange(result.ImpulseBodyAtr, 1.10m, 1.30m);
        Assert.True(result.Permitted);
    }

    [Theory]
    [InlineData(Direction.Buy)]
    [InlineData(Direction.Sell)]
    public void StillRejectsExcessiveExtension(Direction direction)
    {
        var result = FirstEntryTimingPolicy.Evaluate(
            FlatBarsWithLatest(102.8m, 103.2m, 101.2m, 103m, direction), direction);

        Assert.True(result.AtrExtension > .65m);
        Assert.False(result.Permitted);
    }

    [Theory]
    [InlineData(Direction.Buy)]
    [InlineData(Direction.Sell)]
    public void StillRejectsExcessiveImpulse(Direction direction)
    {
        var result = FirstEntryTimingPolicy.Evaluate(
            FlatBarsWithLatest(97.8m, 101.4m, 97.6m, 101.2m, direction), direction);

        Assert.True(result.ImpulseBodyAtr > 1.30m);
        Assert.False(result.Permitted);
    }

    [Fact]
    public void StillRequiresWarmupAndValidAtr()
    {
        Assert.False(FirstEntryTimingPolicy.Evaluate([], Direction.Buy).Permitted);
        var bars = Enumerable.Range(0, 15).Select(i => new StrategyPriceBar(
            DateTimeOffset.UnixEpoch.AddMinutes(i), 100m, 100m, 100m, 100m)).ToArray();
        Assert.False(FirstEntryTimingPolicy.Evaluate(bars, Direction.Buy).Permitted);
    }

    private static StrategyPriceBar[] FlatBarsWithLatest(decimal open, decimal high,
        decimal low, decimal close, Direction direction)
    {
        var bars = Enumerable.Range(0, 15).Select(i => new StrategyPriceBar(
            DateTimeOffset.UnixEpoch.AddMinutes(i), 100m, 101m, 99m, 100m)).ToList();
        bars.Add(new(DateTimeOffset.UnixEpoch.AddMinutes(15), open, high, low, close));
        return direction == Direction.Buy ? bars.ToArray() : bars.Select(bar => new StrategyPriceBar(
            bar.OpenTimeUtc, 200m - bar.Open, 200m - bar.Low,
            200m - bar.High, 200m - bar.Close)).ToArray();
    }

    private static StrategyPriceBar[] Bars(decimal[] closes)
    {
        var start = new DateTimeOffset(2026, 9, 24, 3, 45, 0, TimeSpan.Zero);
        return closes.Select((close, i) => new StrategyPriceBar(start.AddMinutes(i * 5),
            close - .05m, close + .15m, close - .15m, close)).ToArray();
    }
}
