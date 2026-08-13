using TradingSystem.Application.Strategies;

namespace TradingSystem.UnitTests;

public sealed class MarketStructureAnalyzerTests
{
    [Fact]
    public void DetectsHigherHighAndHigherLowStructure()
    {
        var now = DateTimeOffset.UtcNow;
        StrategyPriceBar[] bars =
        [
            new(now, 100, 101, 99, 100), new(now, 100, 102, 100, 101),
            new(now, 101, 103, 100, 102), new(now, 102, 104, 101, 103),
            new(now, 103, 105, 102, 104), new(now, 104, 106, 103, 105)
        ];

        var result = MarketStructureAnalyzer.Analyze(bars);

        Assert.Equal(MarketStructureDirection.Bullish, result.Direction);
        Assert.True(result.Strength > 0.5m);
    }
}
