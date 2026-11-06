namespace TradingSystem.Application.Execution;

public sealed record HeroZeroPairBudgetDecision(int Lots, decimal PurchaseCost,
    decimal EstimatedFees, decimal Budget);

// Sensex expiry pair: both long legs can lose. Budget the whole debit, not an
// assumed offsetting winner or each leg's nominal stop. Nifty keeps its existing sizing.
public static class HeroZeroPairBudgetPolicy
{
    public static HeroZeroPairBudgetDecision Size(decimal callPremium, decimal putPremium,
        int callLotSize, int putLotSize, int maximumLots)
    {
        // Separate specialty-pair debit budget; not the ordinary option sizing rule.
        const decimal budget = 5000m;
        if (callPremium <= 0m || putPremium <= 0m || callLotSize <= 0 || putLotSize <= 0 || maximumLots <= 0)
            return new(0, 0m, 0m, budget);
        var low = 0;
        var high = Math.Min(maximumLots, int.MaxValue / Math.Max(callLotSize, putLotSize));
        decimal Fees(int lots) => PaperTradingCostModel.CalculateOptionCharges([
            new(PaperOptionTransactionSide.Buy, callPremium, callLotSize * lots),
            new(PaperOptionTransactionSide.Buy, putPremium, putLotSize * lots),
            new(PaperOptionTransactionSide.Sell, callPremium, callLotSize * lots),
            new(PaperOptionTransactionSide.Sell, putPremium, putLotSize * lots)], "BSE").Total;
        var debitPerLot = callPremium * callLotSize + putPremium * putLotSize;
        while (low < high)
        {
            var middle = (int)(low + (high - low + 1L) / 2);
            if (debitPerLot * middle + Fees(middle) <= budget) low = middle;
            else high = middle - 1;
        }
        return low == 0 ? new(0, 0m, 0m, budget) : new(low, debitPerLot * low, Fees(low), budget);
    }
}
