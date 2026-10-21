using TradingSystem.Application.Execution;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class OptionOpenInterestEntryPolicyTests
{
    [Fact]
    [Trait("Category", "DeploymentRegression")]
    public void RejectsBuyWhenConcentratedCallWallLeavesInsufficientRoom()
    {
        var result = OptionOpenInterestEntryPolicy.Evaluate(Direction.Buy, 23_020m, 80m,
        [
            new(22_900m, 800m, 1_100m),
            new(23_000m, 900m, 1_000m),
            new(23_100m, 5_000m, 900m),
            new(23_200m, 1_000m, 800m),
            new(23_300m, 900m, 700m)
        ]);

        Assert.False(result.Permitted);
        Assert.True(result.Available);
        Assert.Equal(23_100m, result.BarrierStrike);
        Assert.Equal("CallResistance", result.BarrierKind);
        Assert.Equal(1m, result.RoomToRisk);
    }

    [Fact]
    [Trait("Category", "DeploymentRegression")]
    public void RejectsSellWhenPutOiIsBuildingAtNearbySupport()
    {
        OptionOpenInterestStrike[] previous =
        [
            new(74_300m, 1_000m, 900m),
            new(74_400m, 1_000m, 1_000m),
            new(74_500m, 1_000m, 1_000m)
        ];
        OptionOpenInterestStrike[] current =
        [
            new(74_300m, 1_000m, 900m),
            new(74_400m, 1_000m, 1_300m),
            new(74_500m, 1_000m, 1_000m)
        ];

        var result = OptionOpenInterestEntryPolicy.Evaluate(Direction.Sell,
            74_460m, 60m, current, previous);

        Assert.False(result.Permitted);
        Assert.Equal(74_400m, result.BarrierStrike);
        Assert.Equal(.30m, result.OpenInterestChangePercent);
    }

    [Fact]
    [Trait("Category", "DeploymentRegression")]
    public void PermitsTradeWhenStrongWallLeavesEnoughRoom()
    {
        var result = OptionOpenInterestEntryPolicy.Evaluate(Direction.Buy, 22_900m, 80m,
        [
            new(22_900m, 700m, 1_200m),
            new(23_000m, 800m, 1_000m),
            new(23_100m, 5_000m, 900m),
            new(23_200m, 900m, 800m)
        ]);

        Assert.True(result.Permitted);
        Assert.True(result.Available);
        Assert.Equal(2.5m, result.RoomToRisk);
    }

    [Fact]
    [Trait("Category", "DeploymentRegression")]
    public void MissingChainIsExplicitButDoesNotInventARejection()
    {
        var result = OptionOpenInterestEntryPolicy.Evaluate(Direction.Buy,
            23_000m, 80m, []);

        Assert.True(result.Permitted);
        Assert.False(result.Available);
        Assert.Contains(result.Reasons, value => value.Contains("unavailable"));
    }
}
