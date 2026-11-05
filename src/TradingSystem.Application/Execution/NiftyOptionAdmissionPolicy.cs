using TradingSystem.Application.Broker;
using TradingSystem.Application.Risk;

namespace TradingSystem.Application.Execution;

// The quote-to-risk decision is pure so the paper engine and an as-of replay
// can evaluate the same option contract without broker or database access.
public sealed record NiftyOptionAdmissionInput(
    GrowwQuote Quote,
    bool PermissivePaperExecution,
    decimal MaximumSpreadPercent,
    decimal MaximumPremium,
    decimal MinimumVolume,
    decimal MinimumOpenInterest,
    DateOnly TradingDate,
    DateOnly ExpiryDate,
    decimal StopLossPercent,
    decimal RewardToRiskRatio,
    decimal TickSize,
    int LotSize,
    int ConfiguredLots,
    ProfessionalTradeAssessmentInput ProfessionalContext,
    PaperPortfolioRiskInput PortfolioContext);

public sealed record NiftyOptionAdmissionDecision(
    OptionQuoteValidationResult Pricing,
    decimal StopLoss,
    decimal Target,
    ProfessionalTradePlan? ProfessionalPlan,
    RiskDecisionResult? RiskDecision)
{
    public bool Approved => Pricing.Approved && ProfessionalPlan?.Permitted == true &&
        RiskDecision?.Approved == true;
}

public static class NiftyOptionAdmissionPolicy
{
    public static NiftyOptionAdmissionDecision Evaluate(NiftyOptionAdmissionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var pricing = input.PermissivePaperExecution
            ? OptionPaperTradePricing.ForPermissiveSimulation(input.Quote)
            : OptionPaperTradePricing.Validate(input.Quote,
                input.MaximumSpreadPercent, input.MaximumPremium,
                input.MinimumVolume, input.MinimumOpenInterest);
        if (!pricing.Approved)
            return new(pricing, 0m, 0m, null, null);
        var stopPercent = ExpiryAwareOptionPolicy.StopLossPercent(input.TradingDate,
            input.ExpiryDate, input.StopLossPercent);
        var protective = OptionPaperTradePricing.ProtectivePrices(
            pricing.EntryPrice, stopPercent, input.RewardToRiskRatio,
            input.TickSize);
        var spreadPercent = input.Quote.BidPrice is > 0m &&
            input.Quote.OfferPrice is > 0m
            ? (input.Quote.OfferPrice.Value - input.Quote.BidPrice.Value) /
              input.Quote.OfferPrice.Value * 100m
            : (decimal?)null;
        var roundTripPerLot = PaperTradingCostModel.CalculateOptionCharges([
            new(PaperOptionTransactionSide.Buy, pricing.EntryPrice, input.LotSize),
            new(PaperOptionTransactionSide.Sell, protective.Target, input.LotSize)]).Total;
        var grossRewardPerLot = Math.Max(0m,
            (protective.Target - pricing.EntryPrice) * input.LotSize);
        var riskPerLot = Math.Max(.01m,
            (pricing.EntryPrice - protective.StopLoss) * input.LotSize);
        var professional = ProfessionalTradeFramework.Assess(
            input.ProfessionalContext with
            {
                SpreadPercent = spreadPercent,
                Volume = input.Quote.Volume is { } volume
                    ? decimal.ToInt64(volume) : null,
                OpenInterest = input.Quote.OpenInterest is { } oi
                    ? decimal.ToInt64(oi) : null,
                DaysToExpiry = Math.Max(0, input.ExpiryDate.DayNumber -
                    input.TradingDate.DayNumber),
                AfterCostRewardToRisk = Math.Max(0m,
                    (grossRewardPerLot - roundTripPerLot) / riskPerLot)
            });
        if (!professional.Permitted)
            return new(pricing, protective.StopLoss, protective.Target,
                professional, null);
        var risk = PaperPortfolioRiskPolicy.EvaluateUncappedPaper(
            input.PortfolioContext with
            {
                EntryPrice = pricing.EntryPrice,
                StopLoss = protective.StopLoss,
                LotSize = input.LotSize,
                MaximumLots = input.ConfiguredLots
            });
        risk = risk with { FinalTarget = protective.Target };
        if (risk.Approved)
        {
            var sizing = OptionRiskBudgetPolicy.Size(pricing.EntryPrice, protective.StopLoss,
                input.LotSize, Math.Min(input.ConfiguredLots, risk.ApprovedQuantity / input.LotSize), "NSE");
            var quantity = sizing.Quantity;
            if (quantity == 0)
                return new(pricing, protective.StopLoss, protective.Target,
                    professional, risk with
                    {
                        Approved = false,
                        ApprovedQuantity = 0, RiskAmount = 0m, CapitalExposure = 0m,
                        RejectionReasons = ["One complete lot exceeds the cost-inclusive ₹5,000 planned-risk budget."]
                    });
            risk = risk with
            {
                ApprovedQuantity = quantity,
                RiskAmount = sizing.PlannedLoss,
                CapitalExposure = pricing.EntryPrice * quantity
            };
        }
        return new(pricing, protective.StopLoss, protective.Target,
            professional, risk);
    }
}
