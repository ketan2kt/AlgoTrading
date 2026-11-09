using TradingSystem.Application.Execution;
using TradingSystem.Application.Strategies;

namespace TradingSystem.UnitTests;

public sealed class EntryChainConsistencyPolicyTests
{
    [Theory]
    [InlineData(ProfessionalTradeRegime.Chop)]
    [InlineData(ProfessionalTradeRegime.Exhausted)]
    public void AcceptedSetupCannotBeRevetoedByDescriptiveRegime(ProfessionalTradeRegime regime)
    {
        var decision = EntryChainConsistencyPolicy.Evaluate(regime, new(true, ["Validated completed setup."]));
        Assert.True(decision.Permitted);
        Assert.Equal(ProfessionalTradeRegime.Transition, decision.PlanRegime);
        var context = new ProfessionalTradeAssessmentInput("nifty", decision.PlanRegime,
            .65m, .8m, 1.5m, .30m, 1000, 1000, 2, null, null, 1.5m, false, 0, true);
        Assert.True(ProfessionalTradeFramework.Assess(context).Permitted);
        Assert.False(ProfessionalTradeFramework.Assess(context with { SpreadPercent = 3m }).Permitted);
        Assert.False(ProfessionalTradeFramework.Assess(context with
            { ConsecutiveSameDirectionLosses = 2, PullbackOrRetestConfirmed = false }).Permitted);
    }

    [Theory]
    [InlineData(ProfessionalTradeRegime.Chop)]
    [InlineData(ProfessionalTradeRegime.Exhausted)]
    [InlineData(ProfessionalTradeRegime.Unavailable)]
    public void RejectedSetupIsNeverPromoted(ProfessionalTradeRegime regime)
    {
        Assert.False(EntryChainConsistencyPolicy.Evaluate(regime, new(false, ["Missing confirmation."])).Permitted);
        Assert.False(EntryChainConsistencyPolicy.Evaluate(ProfessionalTradeRegime.Unavailable, new(true, [])).Permitted);
    }

    [Theory]
    [InlineData(ProfessionalTradeRegime.Directional)]
    [InlineData(ProfessionalTradeRegime.Range)]
    [InlineData(ProfessionalTradeRegime.Transition)]
    public void ExistingPermittedPlansRemainUnchanged(ProfessionalTradeRegime regime) =>
        Assert.Equal(regime, EntryChainConsistencyPolicy.Evaluate(regime, new(true, [])).PlanRegime);
}
