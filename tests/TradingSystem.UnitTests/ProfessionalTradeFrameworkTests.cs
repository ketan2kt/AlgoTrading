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
    public void DirectionalParticipationAcceptsCompactContinuationBase()
    {
        var bars = Enumerable.Range(0, 12).Select(index => new StrategyPriceBar(
            DateTimeOffset.UtcNow.AddMinutes(index * 5), 110m - index * .5m,
            110.15m - index * .5m, 109.35m - index * .5m,
            109.55m - index * .5m)).ToList();
        bars.Add(new(bars[^1].OpenTimeUtc.AddMinutes(5), 104.10m, 104.25m, 103.90m, 104.08m));
        bars.Add(new(bars[^1].OpenTimeUtc.AddMinutes(5), 104.08m, 104.20m, 103.88m, 104.02m));
        bars.Add(new(bars[^1].OpenTimeUtc.AddMinutes(5), 104.02m, 104.10m, 103.35m, 103.42m));
        var state = new MarketStateAssessmentV2(IntradayMarketStateV2.BearTrend,
            Direction.Sell, .78m, .58m, .25m, .05m, .10m, .55m, 3.1m, []);

        var decision = DirectionalParticipationPolicy.Evaluate(bars, state,
            107m, 104.25m, 105.1m);

        Assert.Equal(Direction.Sell, decision.Direction);
        Assert.Contains(decision.Reasons, value => value.Contains("continuation base"));
    }

    [Fact]
    public void PersistentVwapSessionOverridesLocalDirectionFlips()
    {
        var bars = new List<StrategyPriceBar>();
        var price = 110m;
        for (var index = 0; index < 20; index++)
        {
            var pullback = index % 3 == 1;
            var close = pullback ? price + .18m : price - .48m;
            bars.Add(new(DateTimeOffset.UtcNow.AddMinutes(index * 5), price,
                Math.Max(price, close) + .12m, Math.Min(price, close) - .12m, close));
            price = close;
        }

        var state = MarketStateRouterV2.Analyze(bars, 108m, 104m, 106m);

        Assert.Equal(IntradayMarketStateV2.BearTrend, state.State);
        Assert.Equal(Direction.Sell, state.Bias);
        Assert.Contains(state.Evidence, value => value.Contains("Persistent-session override"));
    }

    [Fact]
    public void AlternatingChopDoesNotReceivePersistentSessionOverride()
    {
        var bars = Enumerable.Range(0, 20).Select(index =>
        {
            var open = index % 2 == 0 ? 99.5m : 100.5m;
            var close = index % 2 == 0 ? 100.5m : 99.5m;
            return new StrategyPriceBar(DateTimeOffset.UtcNow.AddMinutes(index * 5), open,
                100.7m, 99.3m, close);
        }).ToArray();

        var state = MarketStateRouterV2.Analyze(bars, 100m, 100.05m, 100m);

        Assert.NotEqual(IntradayMarketStateV2.BullTrend, state.State);
        Assert.NotEqual(IntradayMarketStateV2.BearTrend, state.State);
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
