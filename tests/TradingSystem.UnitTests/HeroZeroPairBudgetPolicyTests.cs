using TradingSystem.Application.Execution;
using TradingSystem.Infrastructure.Execution;

namespace TradingSystem.UnitTests;

public sealed class HeroZeroPairBudgetPolicyTests
{
    [Theory]
    [InlineData(100, 100, 1)]
    [InlineData(70, 20, 2)]
    [InlineData(120, 130, 0)]
    public void BothLegsAndFeesMustFitOneBudget(decimal call, decimal put, int expectedLots)
    {
        var result = HeroZeroPairBudgetPolicy.Size(call, put, 20, 20, 5);
        Assert.Equal(expectedLots, result.Lots);
        Assert.True(result.PurchaseCost + result.EstimatedFees <= 5000m);
        if (result.Lots > 0) Assert.True(result.EstimatedFees > 0m);
    }

    [Fact]
    public void SensexPremiumPreferenceStillRequiresLiquidityQualityAndStrikeEligibility()
    {
        var candidate = new HeroZeroCandidateInput(Guid.NewGuid(), "SENSEX26N0579500CE", "CE",
            79500m, new DateOnly(2026, 11, 5), 20, 72.25m, 0m, 0m, 1000m, 1000m, 0m);
        var selected = HeroZeroCandidatePolicy.Select([candidate], "CE", 50m, 15m, 79267.94m, 250m);
        Assert.NotNull(selected);
        Assert.True(selected.Score >= .55m);
        Assert.Null(HeroZeroCandidatePolicy.Select([candidate with { OpenInterest = 0m }],
            "CE", 50m, 15m, 79267.94m, 250m));
        Assert.Null(HeroZeroCandidatePolicy.Select([candidate], "CE", 50m, 15m, 79200m, 250m));
    }

    [Fact]
    public void DoesNotIncreaseLotControlOrChangeNiftyPremiumDefaults()
    {
        Assert.Equal(1, HeroZeroPairBudgetPolicy.Size(20, 20, 20, 20, 1).Lots);
        var settings = new HeroZeroOptions();
        Assert.Equal(20m, settings.TargetPremium);
        Assert.Equal(40m, settings.MaximumPremium);
        Assert.Equal(50m, settings.SensexTargetPremium);
        Assert.Equal(100m, settings.SensexMaximumPremium);
        Assert.Equal(250m, settings.SensexMaximumStrikeDistance);
    }
}
