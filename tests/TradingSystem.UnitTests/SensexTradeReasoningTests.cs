using System.Text.Json;
using TradingSystem.Application.Execution;
using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class SensexTradeReasoningTests
{
    private static readonly StrategyPriceBar[] Bars = Enumerable.Range(0, 12).Select(i =>
        new StrategyPriceBar(DateTimeOffset.UnixEpoch.AddMinutes(i), 100+i, 102+i, 99+i, 101+i)).ToArray();

    [Fact]
    public void SavesReasonsAndMeasurementsWithoutBecomingAGate()
    {
        var result = SensexTradeReasoning.Assess(Bars, Direction.Buy, 3, 100, 90, 110,
            100, 99, 100, 2, 100, 0, 0);
        Assert.True(result.AdvisoryOnly);
        Assert.Equal(9, result.Reasons.Count);
        Assert.Equal(900, result.PlannedNetReward);
        Assert.Equal(1000, result.InitialRisk);
        Assert.Equal(Bars.Length, result.ObservedBars.Count);
        Assert.Equal("Unknown", result.Reasons.Single(x => x.Factor == "Location").Status);
        var restored = JsonSerializer.Deserialize<SensexReasoningSnapshot>(JsonSerializer.Serialize(result));
        Assert.Equal(result.Version, restored!.Version);
        Assert.Equal(result.Reasons.Count, restored.Reasons.Count);
    }

    [Fact]
    public void RecordsBadEconomicsExposureAndMissingDataHonestly()
    {
        var result = SensexTradeReasoning.Assess(Bars, Direction.Sell, 0, 100, 90, 101,
            100, null, null, null, 200, 2, 1);
        Assert.Equal("ReviewConcerns", result.Verdict);
        Assert.Equal("Concern", result.Reasons.Single(x => x.Factor == "Economics").Status);
        Assert.Equal("Concern", result.Reasons.Single(x => x.Factor == "Exposure").Status);
        Assert.Equal("Unknown", result.Reasons.Single(x => x.Factor == "Quote").Status);
        Assert.Null(result.ExtensionAtr);
    }
}
