using TradingSystem.Application.Broker;
using TradingSystem.Application.Execution;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class SensexOptionAdmissionPolicyTests
{
    [Fact]
    public void SharedGeometryAndProfessionalPlanMatchExistingProductionFormula()
    {
        foreach (var entry in new[] { 100m, 143.35m, 550.65m })
        {
            var context = new ProfessionalTradeAssessmentInput("sensex",
                ProfessionalTradeRegime.Directional, .8m, 1m, 2m,
                null, null, null, 3, null, null, 0m, false, 0, true);
            var quote = new GrowwQuote(entry, null, entry - .5m, null, entry, null, 1000m, 5000m, null);
            var result = SensexOptionAdmissionPolicy.Evaluate(new(quote, 10m, .05m, 20, context));
            var stop = Math.Max(.05m, Math.Round(entry * .9m / .05m,
                MidpointRounding.AwayFromZero) * .05m);
            var target = Math.Max(.05m, Math.Round(entry * 1.1m / .05m,
                MidpointRounding.AwayFromZero) * .05m);
            var costs = PaperTradingCostModel.CalculateOptionCharges([
                new(PaperOptionTransactionSide.Buy, entry, 20),
                new(PaperOptionTransactionSide.Sell, target, 20)]).Total;
            var original = ProfessionalTradeFramework.Assess(context with
            {
                SpreadPercent = .5m / entry * 100m, Volume = 1000, OpenInterest = 5000,
                AfterCostRewardToRisk = Math.Max(0m, ((target - entry) * 20 - costs) /
                    Math.Max(.01m, (entry - stop) * 20))
            });
            Assert.Equal(stop, result.Stop);
            Assert.Equal(target, result.Target);
            Assert.Equal(original.Permitted, result.Permitted);
            Assert.Equal(original.Tier, result.Plan!.Tier);
            Assert.Equal(original.QuantityFraction, result.Plan.QuantityFraction);
            Assert.Equal(original.Reasons, result.Plan.Reasons);
        }
    }

    [Fact]
    public void InvalidQuoteCannotCreateAnAdmissionPlan()
    {
        var context = new ProfessionalTradeAssessmentInput("sensex", ProfessionalTradeRegime.Directional,
            .8m, 1m, 2m, null, null, null, 3, null, null, 0m, false, 0, true);
        var result = SensexOptionAdmissionPolicy.Evaluate(new(new GrowwQuote(0m, null,
            null, null, null, null, null, null, null), 10m, .05m, 20, context));
        Assert.False(result.Permitted);
        Assert.Null(result.Plan);
    }
}
