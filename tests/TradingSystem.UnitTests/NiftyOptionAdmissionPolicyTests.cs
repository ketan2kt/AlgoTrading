using TradingSystem.Application.Broker;
using TradingSystem.Application.Execution;
using TradingSystem.Application.Risk;

namespace TradingSystem.UnitTests;

public sealed class NiftyOptionAdmissionPolicyTests
{
    private static readonly DateOnly Day = new(2026, 11, 4);

    [Fact]
    public void AdmissionUsesSamePricingPlanAndPortfolioRiskAsPaperEngine()
    {
        var input = Input();
        var decision = NiftyOptionAdmissionPolicy.Evaluate(input);
        var directPricing = OptionPaperTradePricing.ForPermissiveSimulation(input.Quote);
        var directLevels = OptionPaperTradePricing.ProtectivePrices(
            directPricing.EntryPrice, 10m, 1m, .05m);
        var directRisk = PaperPortfolioRiskPolicy.EvaluateUncappedPaper(
            input.PortfolioContext with
            {
                EntryPrice = directPricing.EntryPrice,
                StopLoss = directLevels.StopLoss,
                LotSize = input.LotSize,
                MaximumLots = input.ConfiguredLots
            });

        Assert.True(decision.Approved);
        Assert.Equal(directPricing.EntryPrice, decision.Pricing.EntryPrice);
        Assert.Equal(directLevels.StopLoss, decision.StopLoss);
        Assert.Equal(directLevels.Target, decision.Target);
        Assert.Equal(directRisk.ApprovedQuantity,
            decision.RiskDecision!.ApprovedQuantity);
        Assert.Equal(150, decision.RiskDecision.ApprovedQuantity);
    }

    [Fact]
    public void QuoteOrKillSwitchRejectionCannotBecomeAReplayCandidate()
    {
        var missingSides = NiftyOptionAdmissionPolicy.Evaluate(Input() with
        {
            PermissivePaperExecution = false,
            Quote = Input().Quote with { BidPrice = null, OfferPrice = null }
        });
        Assert.False(missingSides.Approved);
        Assert.Null(missingSides.RiskDecision);

        var killed = NiftyOptionAdmissionPolicy.Evaluate(Input() with
        {
            PortfolioContext = Input().PortfolioContext with
                { KillSwitchActive = true }
        });
        Assert.False(killed.Approved);
        Assert.Contains(killed.RiskDecision!.RejectionReasons,
            reason => reason.Contains("Kill switch", StringComparison.Ordinal));
    }

    private static NiftyOptionAdmissionInput Input() => new(
        new GrowwQuote(100m, null, 100m, null, 101m, null,
            10_000m, 50_000m, null),
        true, 1.5m, 500m, 100m, 100m,
        Day, Day.AddDays(2), 10m, 1m, .05m, 75, 2,
        new ProfessionalTradeAssessmentInput("nifty",
            ProfessionalTradeRegime.Directional, .80m, 1.1m,
            2m, null, null, null, 0, null, null, 0m,
            false, 0, true),
        new PaperPortfolioRiskInput(0m, 0m, 75, 2,
            100_000m, 1_000_000m, 0m, 0m, 0m,
            100_000m, 0, 8, false, true, true));
}
