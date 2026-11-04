using TradingSystem.Application.Broker;

namespace TradingSystem.Application.Execution;

public sealed record SensexOptionAdmissionInput(GrowwQuote Quote, decimal StopLossPercent,
    decimal TickSize, int LotSize, ProfessionalTradeAssessmentInput ProfessionalContext);
public sealed record SensexOptionAdmissionDecision(decimal Entry, decimal Stop,
    decimal Target, ProfessionalTradePlan? Plan)
{
    public bool Permitted => Plan?.Permitted == true;
}

// Existing Sensex premium geometry and professional assessment, shared with
// historical admission. Quantity remains exactly the administrator's lot size.
public static class SensexOptionAdmissionPolicy
{
    public static (decimal Stop, decimal Target) ProtectivePrices(decimal entry,
        decimal stopPercent, decimal tick)
    {
        var distance = entry * stopPercent / 100m;
        decimal Round(decimal price) => Math.Max(tick,
            Math.Round(price / tick, MidpointRounding.AwayFromZero) * tick);
        return (Round(entry - distance), Round(entry + distance));
    }

    public static SensexOptionAdmissionDecision Evaluate(SensexOptionAdmissionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var quote = input.Quote;
        var entry = quote.OfferPrice ?? quote.LastPrice;
        if (entry <= 0m || input.TickSize <= 0m || input.LotSize <= 0 || input.StopLossPercent <= 0m)
            return new(entry, 0m, 0m, null);
        var (stop, target) = ProtectivePrices(entry, input.StopLossPercent, input.TickSize);
        var spread = quote.BidPrice is > 0m && quote.OfferPrice is > 0m
            ? (quote.OfferPrice.Value - quote.BidPrice.Value) / quote.OfferPrice.Value * 100m
            : (decimal?)null;
        var costs = PaperTradingCostModel.CalculateOptionCharges([
            new(PaperOptionTransactionSide.Buy, entry, input.LotSize),
            new(PaperOptionTransactionSide.Sell, target, input.LotSize)]).Total;
        var reward = Math.Max(0m, (target - entry) * input.LotSize);
        var risk = Math.Max(.01m, (entry - stop) * input.LotSize);
        var plan = ProfessionalTradeFramework.Assess(input.ProfessionalContext with
        {
            SpreadPercent = spread,
            Volume = quote.Volume is { } volume ? decimal.ToInt64(volume) : null,
            OpenInterest = quote.OpenInterest is { } oi ? decimal.ToInt64(oi) : null,
            AfterCostRewardToRisk = Math.Max(0m, (reward - costs) / risk)
        });
        return new(entry, stop, target, plan);
    }
}
