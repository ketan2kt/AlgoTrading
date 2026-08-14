using TradingSystem.Application.Execution;
using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class UnderlyingTrendInvalidationPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 13, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ExitsBullishTradeAfterConfirmedBearishReversal()
    {
        var result = UnderlyingTrendInvalidationPolicy.Evaluate(Direction.Buy,
            BearishBars(), 101m, 103m);

        Assert.True(result.ShouldExit);
        Assert.True(result.SupportingEvidence.Count >= 3);
        Assert.Contains(result.SupportingEvidence, value => value.Contains("swing low"));
    }

    [Fact]
    public void ExitsBearishTradeAfterConfirmedBullishReversal()
    {
        var bars = BearishBars().Reverse().Select((bar, index) =>
            new StrategyPriceBar(Now.AddMinutes(index * 5), 95m + index, 96m + index,
                94.5m + index, 95.5m + index)).ToArray();

        var result = UnderlyingTrendInvalidationPolicy.Evaluate(Direction.Sell, bars, 100m, 98m);

        Assert.True(result.ShouldExit);
        Assert.True(result.SupportingEvidence.Count >= 3);
    }

    [Fact]
    public void DoesNotExitOnSingleAdverseCandle()
    {
        var bars = Enumerable.Range(0, 8).Select(index => new StrategyPriceBar(
            Now.AddMinutes(index * 5), 100m + index, 101m + index,
            99.5m + index, 100.5m + index)).ToArray();
        bars[^1] = bars[^1] with { Open = 108m, High = 108.2m, Low = 104m, Close = 104.5m };

        var result = UnderlyingTrendInvalidationPolicy.Evaluate(Direction.Buy, bars, 106m, 105m);

        Assert.False(result.ShouldExit);
    }

    [Fact]
    public void FailsClosedWhenHistoryIsIncomplete()
    {
        var result = UnderlyingTrendInvalidationPolicy.Evaluate(Direction.Buy,
            BearishBars().Take(4).ToArray(), 97m, 99m);

        Assert.False(result.ShouldExit);
        Assert.Contains("Insufficient", result.SupportingEvidence[0]);
    }

    [Fact]
    public void ZigZagSessionDoesNotExitOnCorrelatedEmaNoise()
    {
        var bars = Enumerable.Range(0, 12).Select(index =>
        {
            var close = index % 2 == 0 ? 101m : 99m;
            return new StrategyPriceBar(Now.AddMinutes(index * 5), 100m,
                close + 1m, close - 1m, close);
        }).ToArray();

        var result = UnderlyingTrendInvalidationPolicy.Evaluate(Direction.Buy,
            bars, 100.5m, 101m);

        Assert.False(result.ShouldExit);
        Assert.Equal(SessionBehaviour.ZigZag, result.Behaviour);
    }

    private static StrategyPriceBar[] BearishBars() =>
    [
        Bar(105, 106, 104, 105), Bar(104, 105, 103, 104),
        Bar(103, 104, 102, 103), Bar(102, 103, 101, 102),
        Bar(101, 102, 100, 101), Bar(100, 101, 99, 100),
        Bar(99, 100, 97, 98), Bar(98, 99, 95, 96)
    ];

    private static StrategyPriceBar Bar(decimal open, decimal high, decimal low, decimal close) =>
        new(Now, open, high, low, close);
}
