namespace TradingSystem.Application.Execution;

public sealed record OptionRiskBudgetDecision(int Quantity, decimal PlannedLoss,
    decimal EstimatedCosts, decimal Budget);

// Normal-session long options: quantity is set by whole lots, not a fixed rupee
// loss ceiling. Risk scales with premium, stop distance and selected quantity.
// Portfolio admission may reduce the requested lots before reaching this policy.
public static class OptionRiskBudgetPolicy
{
    public const string Version = "selected-lots-cost-inclusive-risk-v2";

    public static OptionRiskBudgetDecision Size(decimal entry, decimal stop, int lotSize,
        int maximumLots, string exchange)
    {
        if (entry <= 0m || stop <= 0m || stop >= entry || lotSize <= 0 || maximumLots <= 0 ||
            maximumLots > int.MaxValue / lotSize)
            return new(0, 0m, 0m, 0m);
        var units = checked(maximumLots * lotSize);
        var costs = PaperTradingCostModel.CalculateOptionCharges([
            new(PaperOptionTransactionSide.Buy, entry, units),
            new(PaperOptionTransactionSide.Sell, stop, units)], exchange).Total;
        var plannedLoss = (entry - stop) * units + costs;
        return new(units, plannedLoss, costs, plannedLoss);
    }
}
