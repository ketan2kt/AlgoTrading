using TradingSystem.Application.Execution;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class NaturalGasMiniPositionPolicyTests
{
    [Fact]
    public void PositionIsAlwaysFourLotsOfTwoHundredFiftyUnits()
    {
        Assert.Equal(250, NaturalGasMiniPositionPolicy.ExpectedLotSize);
        Assert.Equal(4, NaturalGasMiniPositionPolicy.FixedLots);
        Assert.Equal(1000, NaturalGasMiniPositionPolicy.FixedQuantity);
        Assert.True(NaturalGasMiniPositionPolicy.IsSupportedContract(250));
        Assert.False(NaturalGasMiniPositionPolicy.IsSupportedContract(1250));
    }

    [Fact]
    public void StopIsAtLeastOneAndHalfAtrFromEntry()
    {
        Assert.Equal(278m, NaturalGasMiniPositionPolicy.WidenStop(Direction.Buy, 281m, 280m, 2m));
        Assert.Equal(284m, NaturalGasMiniPositionPolicy.WidenStop(Direction.Sell, 281m, 282m, 2m));
    }

    [Fact]
    public void TrailingDoesNotTightenBeforeOneAndHalfRisk()
    {
        Assert.Equal(278m, NaturalGasMiniPositionPolicy.ApplyTrailingStop(
            Direction.Buy, 281m, 278m, 285.4m, 3m));
        Assert.Equal(282.5m, NaturalGasMiniPositionPolicy.ApplyTrailingStop(
            Direction.Buy, 281m, 278m, 285.5m, 3m));
    }

    [Fact]
    public void SessionChurnControlsAreConservative()
    {
        Assert.Equal(3, NaturalGasMiniPositionPolicy.MaximumEntriesPerSession);
        Assert.Equal(90, NaturalGasMiniPositionPolicy.ReentryCooldownMinutes);
        Assert.Equal(2m, NaturalGasMiniPositionPolicy.TargetRiskMultiple);
    }
}
