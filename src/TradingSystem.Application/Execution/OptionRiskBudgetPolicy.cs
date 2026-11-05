namespace TradingSystem.Application.Execution;

public sealed record OptionRiskBudgetDecision(int Quantity, decimal PlannedLoss,
    decimal EstimatedCosts, decimal Budget);

// Normal-session long options only. Lot controls are ceilings, never permission
// to widen a stop. Slippage/gaps can still exceed this planned stop-loss budget.
public static class OptionRiskBudgetPolicy
{
    public const decimal MaximumPlannedLoss = 5000m;
    public const string Version = "whole-lot-cost-inclusive-5000-v1";

    public static OptionRiskBudgetDecision Size(decimal entry, decimal stop, int lotSize,
        int maximumLots, string exchange, decimal budget = MaximumPlannedLoss)
    {
        budget = Math.Min(MaximumPlannedLoss, budget);
        if (entry <= 0m || stop <= 0m || stop >= entry || lotSize <= 0 || maximumLots <= 0 || budget <= 0m)
            return new(0, 0m, 0m, budget);
        var low = 0;
        var high = Math.Min(maximumLots, int.MaxValue / lotSize);
        decimal Costs(int quantity) => PaperTradingCostModel.CalculateOptionCharges([
            new(PaperOptionTransactionSide.Buy, entry, quantity),
            new(PaperOptionTransactionSide.Sell, stop, quantity)], exchange).Total;
        while (low < high)
        {
            var middle = low + (high - low + 1L) / 2;
            var quantity = checked((int)middle * lotSize);
            if ((entry - stop) * quantity + Costs(quantity) <= budget) low = (int)middle;
            else high = (int)middle - 1;
        }
        if (low == 0) return new(0, 0m, 0m, budget);
        var units = checked(low * lotSize);
        var costs = Costs(units);
        return new(units, (entry - stop) * units + costs, costs, budget);
    }
}
