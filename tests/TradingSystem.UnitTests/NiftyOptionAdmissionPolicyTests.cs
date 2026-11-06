using TradingSystem.Application.Broker;
using TradingSystem.Application.Execution;
using TradingSystem.Application.Risk;

namespace TradingSystem.UnitTests;

public sealed class NiftyOptionAdmissionPolicyTests
{
    [Fact]
    public void DailyLossSettingIsCapturedWithoutChangingLegacyDefaultReceiptJson()
    {
        var settings = System.Text.Json.JsonSerializer.Deserialize<NiftyOptionAdmissionSettings>("{}")!;
        Assert.False(settings.EnforceDailyLossLimit);
        Assert.DoesNotContain("EnforceDailyLossLimit", System.Text.Json.JsonSerializer.Serialize(settings), StringComparison.Ordinal);
        var json = System.Text.Json.JsonSerializer.Serialize(settings with { EnforceDailyLossLimit = true });
        Assert.True(System.Text.Json.JsonSerializer.Deserialize<NiftyOptionAdmissionSettings>(json)!.EnforceDailyLossLimit);
    }
    private static readonly DateOnly Day = new(2026, 11, 4);

    [Fact]
    public void AdmissionUsesSamePricingPlanAndPortfolioRiskAsPaperEngine()
    {
        var input = Input();
        var decision = NiftyOptionAdmissionPolicy.Evaluate(input);
        var directPricing = OptionPaperTradePricing.ForPermissiveSimulation(input.Quote);
        var directLevels = OptionPaperTradePricing.ProtectivePrices(
            directPricing.EntryPrice, 10m, 1m, .05m);
        var directRisk = PaperPortfolioRiskPolicy.Evaluate(
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

    [Theory]
    [InlineData("capital")]
    [InlineData("positions")]
    [InlineData("loss")]
    public void ConfiguredPortfolioLimitsCannotBeBypassed(string limit)
    {
        var input = Input();
        var context = limit switch
        {
            "capital" => input.PortfolioContext with { MaximumPortfolioExposure = 1000m },
            "positions" => input.PortfolioContext with { OpenPositions = 8 },
            _ => input.PortfolioContext with { MaximumDailyLoss = 5000m, DailyRealisedPnl = -5100m }
        };
        Assert.False(NiftyOptionAdmissionPolicy.Evaluate(input with { PortfolioContext = context }).Approved);
        if (limit == "loss")
            Assert.True(NiftyOptionAdmissionPolicy.Evaluate(input with
                { PortfolioContext = context with { DailyLossLimitOverridden = true } }).Approved);
    }

    [Fact]
    public void ApprovedLotSettingIsACeilingAndIncludesStopExitCosts()
    {
        var input = Input() with { ConfiguredLots = 20 };
        var decision = NiftyOptionAdmissionPolicy.Evaluate(input);
        Assert.True(decision.Approved);
        var sized = OptionRiskBudgetPolicy.Size(decision.Pricing.EntryPrice, decision.StopLoss,
            input.LotSize, input.ConfiguredLots, "NSE");
        Assert.Equal(sized.Quantity, decision.RiskDecision!.ApprovedQuantity);
        Assert.Equal(sized.PlannedLoss, decision.RiskDecision.RiskAmount);
        Assert.Equal(input.LotSize * input.ConfiguredLots, sized.Quantity);
        Assert.True(decision.RiskDecision.RiskAmount > 5000m);
        var legacyCap = NiftyOptionAdmissionPolicy.Evaluate(input with
            { PortfolioContext = input.PortfolioContext with { MaximumRiskPerTrade = 1m } });
        Assert.Equal(sized.Quantity, legacyCap.RiskDecision!.ApprovedQuantity);
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
