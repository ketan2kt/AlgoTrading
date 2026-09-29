using TradingSystem.Application.Execution;
using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class StructuralTransitionResearchPolicyTests
{
    [Fact]
    public void AcceptsBullishBreakAndFirstRetestBeforeChasing()
    {
        var bars = BaseBars(100m);
        bars.Add(Bar(18, 100.0m, 100.9m, 99.9m, 100.8m));
        bars.Add(Bar(19, 100.8m, 101.0m, 100.45m, 100.9m));
        bars.Add(Bar(20, 100.40m, 100.70m, 100.30m, 100.58m));

        var result = StructuralTransitionResearchPolicy.Evaluate(bars);

        Assert.Equal(Direction.Buy, result.Direction);
        Assert.True(result.StructuralStop < bars[^1].Close);
        Assert.Equal(.72m, result.Confidence);
    }

    [Fact]
    public void RejectsRangeNoiseWithoutDisplacedBreak()
    {
        var bars = Enumerable.Range(0, 24).Select(index => Bar(index,
            100m + (index % 2 == 0 ? -.10m : .10m), 100.25m, 99.75m,
            100m + (index % 2 == 0 ? .10m : -.10m))).ToArray();

        var result = StructuralTransitionResearchPolicy.Evaluate(bars);

        Assert.Null(result.Direction);
    }

    [Fact]
    public void DailyResearchCapacityIsBoundedAndRequiresNoOpenPosition()
    {
        Assert.True(StructuralTransitionResearchPolicy.CanOpen(0, false));
        Assert.False(StructuralTransitionResearchPolicy.CanOpen(2, false));
        Assert.False(StructuralTransitionResearchPolicy.CanOpen(0, true));
    }

    private static List<StrategyPriceBar> BaseBars(decimal price) =>
        Enumerable.Range(0, 18).Select(index => Bar(index, price - .10m, price + .30m,
            price - .30m, price + (index % 2 == 0 ? .05m : -.05m))).ToList();

    private static StrategyPriceBar Bar(int minute, decimal open, decimal high,
        decimal low, decimal close) => new(
        new DateTimeOffset(2026, 9, 29, 9, 15, 0, TimeSpan.Zero).AddMinutes(minute * 5),
        open, high, low, close);
}
