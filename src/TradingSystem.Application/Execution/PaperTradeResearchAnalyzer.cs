namespace TradingSystem.Application.Execution;

public sealed record PaperTradeResearchInput(int Quantity, decimal EntryPrice, decimal ExitPrice,
    decimal GrossPnl, decimal EstimatedCosts, decimal RealisedPnl, string ExitReason,
    IReadOnlyList<decimal> OpenPositionPrices,
    IReadOnlyList<decimal> PostExitIncrementalPnls);

public static class PaperTradeResearchAnalyzer
{
    public static TradeExitQualityMetrics Analyze(PaperTradeResearchInput input)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(input.Quantity);
        if (input.EntryPrice <= 0 || input.ExitPrice <= 0)
            throw new ArgumentOutOfRangeException(nameof(input), "Trade prices must be positive.");

        var prices = input.OpenPositionPrices.Where(value => value > 0)
            .Append(input.EntryPrice).Append(input.ExitPrice).ToArray();
        var maximumFavourable = Math.Max(0m, (prices.Max() - input.EntryPrice) * input.Quantity);
        var maximumAdverse = Math.Max(0m, (input.EntryPrice - prices.Min()) * input.Quantity);
        var giveback = Math.Max(0m, maximumFavourable - input.GrossPnl);
        var capturedRatio = maximumFavourable <= 0 ? 0m
            : Math.Clamp(input.GrossPnl / maximumFavourable, 0m, 1m);
        var bestPostExit = input.PostExitIncrementalPnls.Count == 0 ? 0m
            : Math.Max(0m, input.PostExitIncrementalPnls.Max());
        var materiality = Math.Max(input.EstimatedCosts, input.Quantity);
        var assessment = prices.Length < 4 ? "InsufficientPricePath"
            : bestPostExit > materiality ? "PotentialEarlyExit"
            : maximumFavourable > materiality && giveback >= maximumFavourable * 0.50m
                ? "MaterialProfitGiveback"
            : input.ExitReason.Contains("Stop", StringComparison.OrdinalIgnoreCase) &&
              bestPostExit <= materiality ? "StopSupportedByFollowUp"
            : "ExitWithinObservedRange";

        return new(maximumFavourable, maximumAdverse, giveback, capturedRatio,
            bestPostExit, prices.Length, assessment);
    }

    public static decimal ProfitFactor(IEnumerable<decimal> pnls)
    {
        var values = pnls.ToArray();
        var profit = values.Where(value => value > 0).Sum();
        var loss = Math.Abs(values.Where(value => value < 0).Sum());
        return loss == 0 ? profit > 0 ? 999m : 0m : profit / loss;
    }
}
