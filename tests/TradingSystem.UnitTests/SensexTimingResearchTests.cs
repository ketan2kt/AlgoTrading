using System.Text.Json;
using TradingSystem.Application.Execution;
using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class SensexTimingResearchTests
{
    [Theory]
    [InlineData(Direction.Buy, 115, 1.5)]
    [InlineData(Direction.Sell, 75, 1.5)]
    public void MeasuresDirectionalExtensionWithoutInventingSetupAge(Direction direction, int close, double expected)
    {
        var time = new DateTimeOffset(2026, 9, 4, 4, 0, 0, TimeSpan.Zero);
        StrategyPriceBar[] bars = [new(time, 95, 100, 90, 95),
            new(time.AddMinutes(1), close, close, close, close)];
        var json = JsonSerializer.SerializeToElement(SensexTimingResearch.Observe(
            bars, direction, 10, time.AddMinutes(2), 60));
        Assert.Equal((decimal)expected, json.GetProperty("extensionAtr").GetDecimal());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("setupAgeSeconds").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("roomAtr").ValueKind);
        Assert.True(json.GetProperty("advisoryOnly").GetBoolean());
    }

    [Fact]
    public void InvalidLegacyAuditIsIgnored()
    {
        Assert.Null(SensexResearchAuditParser.Parse("not json", "price"));
        Assert.Null(SensexResearchAuditParser.Parse("{}", "price"));
        Assert.Null(SensexResearchAuditParser.Parse("[]", "price"));
    }

    [Fact]
    public void ReadsRealPriceAndPostExitValues()
    {
        var id = Guid.NewGuid();
        var json = JsonSerializer.Serialize(new { positionId = id, price = 123m, incrementalAfterExit = -40m });
        Assert.Equal((id, 123m), SensexResearchAuditParser.Parse(json, "price")!.Value);
        Assert.Equal((id, -40m), SensexResearchAuditParser.Parse(json, "incrementalAfterExit")!.Value);
    }
}
