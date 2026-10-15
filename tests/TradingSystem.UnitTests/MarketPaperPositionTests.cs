using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class MarketPaperPositionTests
{
    [Fact]
    public void LongPositionTrailsUpAndClosesNetOfCosts()
    {
        var opened = DateTimeOffset.UtcNow;
        var position = new MarketPaperPosition(Guid.NewGuid(), "sensex", Guid.NewGuid(), Guid.NewGuid(),
            "momentum", Direction.Buy, 100, 100m, 90m, 110m, opened);

        position.Mark(108m, 105m);
        position.Mark(106m, 103m);
        position.Close(106m, 50m, "TrailingStopHit", opened.AddMinutes(10));

        Assert.Equal(105m, position.StopLoss);
        Assert.Equal(550m, position.RealisedPnl);
        Assert.Equal("TrailingStopHit", position.Status);
    }

    [Fact]
    public void ShortPositionNeverWidensTrailedStop()
    {
        var position = new MarketPaperPosition(Guid.NewGuid(), "natural-gas", Guid.NewGuid(), Guid.NewGuid(),
            "momentum", Direction.Sell, 10, 300m, 310m, 290m, DateTimeOffset.UtcNow);

        position.Mark(294m, 296m);
        position.Mark(297m, 302m);

        Assert.Equal(296m, position.StopLoss);
    }

    [Fact]
    public void PartialProfitAndRunnerAreAccountedNetOfSeparateCosts()
    {
        var opened = DateTimeOffset.UtcNow;
        var position = new MarketPaperPosition(Guid.NewGuid(), "sensex", Guid.NewGuid(), Guid.NewGuid(),
            "directional", Direction.Buy, 100, 100m, 90m, 120m, opened);

        position.TakePartial(112m, 40, 20m);
        position.Close(118m, 30m, "TrailingStopHit", opened.AddMinutes(20));

        Assert.Equal(0, position.RemainingQuantity);
        Assert.Equal(40, position.PartialExitQuantity);
        Assert.Equal(112m, position.PartialExitPrice);
        Assert.Equal(50m, position.CostsPaid);
        Assert.Equal(1510m, position.RealisedPnl);
    }
}
