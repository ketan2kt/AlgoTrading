namespace TradingSystem.Application.Execution;

public static class PaperTradingCostModel
{
    public static decimal EstimateRoundTripCost(decimal entryPrice, decimal exitPrice,
        int quantity, decimal costBasisPoints)
    {
        if (entryPrice <= 0 || exitPrice <= 0 || quantity <= 0 || costBasisPoints < 0)
            throw new ArgumentOutOfRangeException(nameof(entryPrice));
        return (entryPrice + exitPrice) * quantity * costBasisPoints / 10_000m;
    }
}
