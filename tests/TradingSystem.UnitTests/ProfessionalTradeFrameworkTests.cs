using TradingSystem.Application.Execution;
using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class ProfessionalTradeFrameworkTests
{
    [Fact]
    public void HighConvictionDirectionalSetupKeepsFullSizeAndRunner()
    {
        var plan = ProfessionalTradeFramework.Assess(Input(
            ProfessionalTradeRegime.Directional, confidence: .78m, volume: 1.2m,
            room: 1.8m, pullback: true));

        Assert.True(plan.Permitted);
        Assert.Equal(ProfessionalSetupTier.HighConviction, plan.Tier);
        Assert.Equal(1m, plan.QuantityFraction);
        Assert.True(plan.PermitWinnerAddOn);
        Assert.Equal(1.25m, plan.PartialProfitR);
        Assert.Equal(1.25m, plan.TrailDistanceR);
    }

    [Fact]
    public void CorrelatedExposureReducesRatherThanDuplicatesRisk()
    {
        var plan = ProfessionalTradeFramework.Assess(Input(
            ProfessionalTradeRegime.Directional, .78m, 1.2m, 1.8m, true,
            correlated: true));

        Assert.True(plan.Permitted);
        Assert.Equal(.50m, plan.QuantityFraction);
        Assert.Contains(plan.Reasons, value => value.Contains("Correlated"));
    }

    [Theory]
    [InlineData(ProfessionalTradeRegime.Chop)]
    [InlineData(ProfessionalTradeRegime.Exhausted)]
    public void RejectsChopAndExhaustedMoves(ProfessionalTradeRegime regime)
    {
        Assert.False(ProfessionalTradeFramework.Assess(Input(
            regime, .80m, 1.5m, 2m, true)).Permitted);
    }

    [Fact]
    public void RepeatedLossRequiresFreshStructure()
    {
        var input = Input(ProfessionalTradeRegime.Directional, .75m, 1m, 1.5m, false) with
        {
            ConsecutiveSameDirectionLosses = 2
        };
        Assert.False(ProfessionalTradeFramework.Assess(input).Permitted);
        Assert.True(ProfessionalTradeFramework.Assess(input with
            { PullbackOrRetestConfirmed = true }).Permitted);
    }

    [Fact]
    public void LowAfterCostRewardRiskIsAuditedWithoutBlockingPaperParticipation()
    {
        var plan = ProfessionalTradeFramework.Assess(Input(
            ProfessionalTradeRegime.Directional, .70m, 1m, 1.5m, true) with
        {
            AfterCostRewardToRisk = .88m
        });

        Assert.True(plan.Permitted);
        Assert.Contains(plan.Reasons, value => value.Contains("Advisory") && value.Contains("0.88"));
    }

    [Fact]
    public void QuantityScalingPreservesWholeLots()
    {
        Assert.Equal(150, ProfessionalTradeFramework.ScaleQuantity(250, 50, .65m));
        Assert.Equal(50, ProfessionalTradeFramework.ScaleQuantity(50, 50, .25m));
    }

    [Fact]
    public void DirectionalParticipationWaitsForPullbackThenResumption()
    {
        var bars = Enumerable.Range(0, 15).Select(index => new StrategyPriceBar(
            DateTimeOffset.UtcNow.AddMinutes(index * 5), 100m - index,
            100.5m - index, 99.5m - index, 99.8m - index)).ToArray();
        bars[^2] = bars[^2] with { Open = 86.8m, Close = 87.4m, High = 87.6m, Low = 86.5m };
        bars[^1] = bars[^1] with { Open = 87.2m, Close = 85.8m, High = 87.3m, Low = 85.5m };
        var state = new MarketStateAssessmentV2(IntradayMarketStateV2.BearTrend,
            Direction.Sell, .76m, .62m, .20m, .10m, .10m, .50m, 2.5m, []);

        var decision = DirectionalParticipationPolicy.Evaluate(bars, state,
            93m, 88m, 91m);

        Assert.Equal(Direction.Sell, decision.Direction);
        Assert.True(decision.StructuralStop > bars[^1].Close);
        Assert.True(decision.Target < bars[^1].Close);
    }

    [Fact]
    public void DirectionalExitPlanProtectsProfitWithoutTightOneRChoking()
    {
        var plan = ProfessionalExitPolicy.For(ProfessionalTradeRegime.Directional);
        Assert.Equal(.65m, plan.ProfitLockTriggerR);
        Assert.Equal(1.25m, plan.TrailDistanceR);
        Assert.True(plan.ExtendTarget);
    }

    [Fact]
    public void WinnerAddOnRequiresProfitContinuationAndSingleUse()
    {
        Assert.True(WinnerAddOnPolicy.Evaluate(1.1m, true, false, 1_000m, 200m).Permitted);
        Assert.False(WinnerAddOnPolicy.Evaluate(.9m, true, false, 1_000m, 200m).Permitted);
        Assert.False(WinnerAddOnPolicy.Evaluate(1.1m, false, false, 1_000m, 200m).Permitted);
        Assert.False(WinnerAddOnPolicy.Evaluate(1.1m, true, true, 1_000m, 200m).Permitted);
    }

    private static ProfessionalTradeAssessmentInput Input(ProfessionalTradeRegime regime,
        decimal confidence, decimal volume, decimal room, bool pullback,
        bool correlated = false) => new("nifty", regime, confidence, volume, room,
        .4m, 1_000, 5_000, 3, .55m, .20m, 1.8m, correlated, 0, pullback);
}
