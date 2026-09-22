using TradingSystem.Application.Execution;
using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class SensexEarlyPullbackResearchPolicyTests
{
    [Fact]
    public void FindsBearishRejectionNearFastEmaBeforeAnotherBreakdown()
    {
        var start = new DateTimeOffset(2026, 9, 22, 4, 0, 0, TimeSpan.Zero);
        var closes = new decimal[] { 760, 758, 756, 754, 752, 750, 748, 746, 744, 742,
            740, 738, 736, 734, 732, 731, 732, 733, 731 };
        var bars = closes.Select((close, i) => new StrategyPriceBar(start.AddMinutes(i),
            i == closes.Length - 1 ? close + 2 : close + 1, close + 3, close - 3, close)).ToArray();

        var result = SensexEarlyPullbackResearchPolicy.Evaluate(bars);

        Assert.Equal(Direction.Sell, result.Direction);
        Assert.Contains(result.Reasons, x => x.Contains("before a new breakdown"));
    }

    [Fact]
    public void DoesNotInventEntryInFlatNoise()
    {
        var start = new DateTimeOffset(2026, 9, 22, 4, 0, 0, TimeSpan.Zero);
        var bars = Enumerable.Range(0, 20).Select(i => new StrategyPriceBar(start.AddMinutes(i),
            100, 101, 99, i % 2 == 0 ? 100.2m : 99.8m)).ToArray();

        Assert.Null(SensexEarlyPullbackResearchPolicy.Evaluate(bars).Direction);
    }
}
