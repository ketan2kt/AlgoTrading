using System.Text.Json;
using TradingSystem.Application.Execution;
using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class BankContextRetestTests
{
    private static (StrategyPriceBar[] Bars, Direction Direction, bool Expected)[] Cases()
    {
        using var stream=typeof(BankContextRetestTests).Assembly.GetManifestResourceStream(
            "TradingSystem.UnitTests.RegressionFixtures.bank-context-retest-202611.json")!;
        using var doc=JsonDocument.Parse(stream);
        return doc.RootElement.EnumerateArray().Select(x=>(
            x.GetProperty("bars").EnumerateArray().Select(b=>new StrategyPriceBar(b.GetProperty("at").GetDateTimeOffset(),
                b.GetProperty("o").GetDecimal(),b.GetProperty("h").GetDecimal(),b.GetProperty("l").GetDecimal(),b.GetProperty("c").GetDecimal())).ToArray(),
            Enum.Parse<Direction>(x.GetProperty("direction").GetString()!),x.GetProperty("expected").GetBoolean())).ToArray();
    }

    [Fact]
    public void HistoricalRetestRetainsWinnerButDoesNotReadmitThreeAvoidedLosses()
    {
        foreach(var (bars,direction,expected) in Cases())
            Assert.Equal(expected,SharedMarketContextPolicy.HasCompletedBreakoutRetest(bars,direction));
    }

    [Fact]
    public void PatternIsDirectionSymmetricAndNotASpecificStrikeOrDateException()
    {
        foreach(var (bars,direction,expected) in Cases())
        {
            var mirror=bars.Select(b=>new StrategyPriceBar(b.OpenTimeUtc.AddDays(7),130000m-b.Open,
                130000m-b.Low,130000m-b.High,130000m-b.Close)).ToArray();
            Assert.Equal(expected,SharedMarketContextPolicy.HasCompletedBreakoutRetest(mirror,
                direction==Direction.Buy?Direction.Sell:Direction.Buy));
        }
    }

    [Fact]
    public void BrokenRetestOrMissingMinuteCannotSupplyConfirmation()
    {
        var bars=Cases()[0].Bars;
        var retest=bars.Where(b=>b.OpenTimeUtc.Minute is >=20 and <25).ToArray();
        var broken=bars.Select(b=>b==retest[^1]?b with {Low=b.Low-100m,Close=b.Close-100m}:b).ToArray();
        Assert.False(SharedMarketContextPolicy.HasCompletedBreakoutRetest(broken,Direction.Buy));
        Assert.False(SharedMarketContextPolicy.HasCompletedBreakoutRetest(bars.Where(b=>b!=retest[2]).ToArray(),Direction.Buy));
        Assert.False(SharedMarketContextPolicy.HasCompletedBreakoutRetest(bars,Direction.Buy,300));
        Assert.False(SharedMarketContextPolicy.HasCompletedBreakoutRetest(bars.Where(b=>b.OpenTimeUtc<bars[^1].OpenTimeUtc.AddMinutes(-2)).ToArray(),Direction.Buy));
    }
}
