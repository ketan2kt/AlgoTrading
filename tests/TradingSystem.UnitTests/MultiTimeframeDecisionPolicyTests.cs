using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class MultiTimeframeDecisionPolicyTests
{
    [Fact]
    public void RisingStructureAlignsBuyCandidateAcrossFrames()
    {
        var start = new DateTimeOffset(2026, 10, 1, 3, 45, 0, TimeSpan.Zero);
        var bars = Enumerable.Range(0, 60).Select(index =>
            new StrategyPriceBar(start.AddMinutes(index), 100m + index,
                101m + index, 99m + index, 100.8m + index)).ToArray();
        var result = MultiTimeframeDecisionPolicy.Analyze(bars, Direction.Buy, 100m, 170m);
        Assert.Equal(Direction.Buy, result.Consensus);
        Assert.True(result.CandidateAligned);
        Assert.Equal(3, result.Timeframes.Count);
    }

    [Fact]
    public void OppositeCandidateIsNotAligned()
    {
        var start = DateTimeOffset.UtcNow.AddHours(-1);
        var bars = Enumerable.Range(0, 60).Select(index =>
            new StrategyPriceBar(start.AddMinutes(index), 200m-index,
                201m-index, 199m-index, 199.5m-index)).ToArray();
        var result = MultiTimeframeDecisionPolicy.Analyze(bars, Direction.Buy, 120m, 210m);
        Assert.Equal(Direction.Sell, result.Consensus);
        Assert.False(result.CandidateAligned);
    }
}
