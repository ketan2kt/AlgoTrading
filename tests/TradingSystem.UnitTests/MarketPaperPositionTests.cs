using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class MarketPaperPositionTests
{
    [Fact]
    public void LegacyPriceSampleBucketNowTracksTwoSecondQuoteCadence()
    {
        var start = DateTimeOffset.FromUnixTimeSeconds(0);

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1), PaperTradePriceSample.BucketFor(start));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1),
            PaperTradePriceSample.BucketFor(start.AddMilliseconds(1_999)));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(3),
            PaperTradePriceSample.BucketFor(start.AddSeconds(2)));
    }

    [Fact]
    public void LongPositionTrailsUpAndClosesNetOfCosts()
    {
        var opened = DateTimeOffset.UtcNow;
        var position = new MarketPaperPosition(Guid.NewGuid(), "sensex", Guid.NewGuid(), Guid.NewGuid(),
            "momentum", Direction.Buy, 100, 100m, 90m, 110m, opened);

        position.Mark(108m, 105m, opened.AddMinutes(1));
        position.Mark(106m, 103m, opened.AddMinutes(2));
        position.Close(106m, 50m, "TrailingStopHit", opened.AddMinutes(10));

        Assert.Equal(105m, position.StopLoss);
        Assert.Equal(550m, position.RealisedPnl);
        Assert.Equal("TrailingStopHit", position.Status);
        Assert.Equal(800m, position.MaximumFavourablePnl);
        Assert.Equal(0m, position.MaximumAdversePnl);
        Assert.Equal(opened.AddMinutes(1), position.MaximumFavourableAtUtc);
        Assert.Equal(4, position.ExcursionSampleCount);
    }

    [Fact]
    public void ShortPositionNeverWidensTrailedStop()
    {
        var position = new MarketPaperPosition(Guid.NewGuid(), "natural-gas", Guid.NewGuid(), Guid.NewGuid(),
            "momentum", Direction.Sell, 10, 300m, 310m, 290m, DateTimeOffset.UtcNow);

        var opened = DateTimeOffset.UtcNow;
        position.Mark(294m, 296m, opened.AddMinutes(1));
        position.Mark(297m, 302m, opened.AddMinutes(2));

        Assert.Equal(296m, position.StopLoss);
        Assert.Equal(60m, position.MaximumFavourablePnl);
        Assert.Equal(0m, position.MaximumAdversePnl);
    }

    [Fact]
    public void AdverseAndFavourableExcursionsAreTrackedForShortPositions()
    {
        var opened = DateTimeOffset.UtcNow;
        var position = new MarketPaperPosition(Guid.NewGuid(), "nifty", Guid.NewGuid(), Guid.NewGuid(),
            "momentum", Direction.Sell, 10, 100m, 110m, 80m, opened);

        position.Mark(104m, 110m, opened.AddSeconds(2));
        position.Mark(95m, 105m, opened.AddSeconds(4));
        position.Mark(103m, 105m, opened.AddSeconds(6));

        Assert.Equal(50m, position.MaximumFavourablePnl);
        Assert.Equal(-40m, position.MaximumAdversePnl);
        Assert.Equal(opened.AddSeconds(4), position.MaximumFavourableAtUtc);
        Assert.Equal(opened.AddSeconds(2), position.MaximumAdverseAtUtc);
    }

    [Fact]
    public void PartialProfitAndRunnerAreAccountedNetOfSeparateCosts()
    {
        var opened = DateTimeOffset.UtcNow;
        var position = new MarketPaperPosition(Guid.NewGuid(), "sensex", Guid.NewGuid(), Guid.NewGuid(),
            "directional", Direction.Buy, 100, 100m, 90m, 120m, opened);

        position.TakePartial(112m, 40, 20m, opened.AddMinutes(10));
        position.Close(118m, 30m, "TrailingStopHit", opened.AddMinutes(20));

        Assert.Equal(0, position.RemainingQuantity);
        Assert.Equal(40, position.PartialExitQuantity);
        Assert.Equal(112m, position.PartialExitPrice);
        Assert.Equal(50m, position.CostsPaid);
        Assert.Equal(1510m, position.RealisedPnl);
    }
}
