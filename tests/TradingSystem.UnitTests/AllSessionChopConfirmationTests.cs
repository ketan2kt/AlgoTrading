using TradingSystem.Application.Execution;
using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class AllSessionChopConfirmationTests
{
    private static readonly MarketStructureQualitySnapshot Choppy = new(
        MarketStructureQualityState.StructuredRange, Direction.Buy, .2m, .6m, .3m,
        2m, null, .3m, false, []);

    [Fact]
    public void Recorded1355SensexCandidateLackedCompletedHigherTimeframeBreak()
    {
        var bars = FiveBars([(77211.78m, 77253.65m, 77195.83m, 77199.41m),
            (77198.02m, 77208.64m, 77150.47m, 77168.70m),
            (77165.37m, 77171.10m, 77145.04m, 77171.10m)]);
        Assert.False(MiddayChopConfirmationPolicy.Evaluate(new(13, 55), bars,
            Direction.Buy, Choppy, 300, includeOtherHours: true).Permitted);
    }

    [Fact]
    public void Recorded1425SensexCandidateDidNotCloseAbovePreviousHigh()
    {
        var bars = FiveBars([(77109.83m, 77138.98m, 77068.47m, 77096.53m),
            (77098.83m, 77185.01m, 77092.79m, 77183.95m),
            (77184.47m, 77185.98m, 77154.41m, 77184.98m)]);
        Assert.False(MiddayChopConfirmationPolicy.Evaluate(new(14, 25), bars,
            Direction.Buy, Choppy, 300, includeOtherHours: true).Permitted);
    }

    [Fact]
    public void Recorded1331WinningContinuationRetainsItsConfirmation()
    {
        var bars = FiveBars([(77026.37m, 77055.13m, 77018.39m, 77053.85m),
            (77057.95m, 77062.37m, 77035.74m, 77045.31m),
            (77040.22m, 77105.12m, 77039.69m, 77087.79m)]);
        Assert.True(MiddayChopConfirmationPolicy.Evaluate(new(13, 31), bars,
            Direction.Buy, Choppy, 300, includeOtherHours: true).Permitted);
    }

    [Fact]
    public void DevelopingTrendDoesNotAcquireAnExtraFiveMinuteWait()
    {
        Assert.True(MiddayChopConfirmationPolicy.Evaluate(new(14, 25), [], Direction.Buy,
            Choppy with { State = MarketStructureQualityState.DevelopingTrend, ChopScore = .3m },
            60, includeOtherHours: true).Permitted);
    }

    [Fact]
    public void ConfirmedBearishBreakUsesTheSameLateSessionRule()
    {
        var bars = FiveBars([(101m, 102m, 100m, 101m),
            (101m, 101.5m, 99m, 100m), (100m, 100m, 97m, 98m)]);
        Assert.True(MiddayChopConfirmationPolicy.Evaluate(new(14, 25), bars,
            Direction.Sell, Choppy with { ObservedBias = Direction.Sell },
            300, includeOtherHours: true).Permitted);
    }

    private static StrategyPriceBar[] FiveBars((decimal Open, decimal High, decimal Low, decimal Close)[] source) =>
        source.Select((bar, index) => new StrategyPriceBar(DateTimeOffset.UnixEpoch.AddMinutes(index * 5),
            bar.Open, bar.High, bar.Low, bar.Close)).ToArray();
}
