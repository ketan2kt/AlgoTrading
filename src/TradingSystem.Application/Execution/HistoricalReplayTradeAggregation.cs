namespace TradingSystem.Application.Execution;

public static class HistoricalReplayTradeAggregation
{
    // A partial fill is an exit transaction, not a completed trade. Combine
    // its net and costs with the terminal exit before exposing closed-position
    // state to entry/cooldown policies or computing trade-level statistics.
    public static IReadOnlyList<HistoricalReplayFill> CompletedPositions(
        IReadOnlyList<HistoricalReplayFill> fills) => fills
        .GroupBy(value => (value.Symbol, value.EntryAtUtc,
            value.DecisionAtUtc, value.Strategy))
        .Select(group =>
        {
            var terminal = group.SingleOrDefault(value => !value.IsPartialExit);
            if (terminal is null) return null;
            var all = group.ToArray();
            if (all.Length == 1) return terminal;
            var originalQuantity = terminal.OriginalQuantity > 0
                ? terminal.OriginalQuantity : all.Sum(value => value.Quantity);
            return terminal with
            {
                Quantity = originalQuantity,
                OriginalQuantity = originalQuantity,
                GrossPnl = all.Sum(value => value.GrossPnl),
                Costs = all.Sum(value => value.Costs),
                NetPnl = all.Sum(value => value.NetPnl),
                // Peaks from different tranche intervals are not a single
                // observed, executable whole-position peak.
                ObservedPeakNetPnl = null,
                ObservedAdverseNetPnl = null,
                SampledProfitGiveback = null
            };
        })
        .Where(value => value is not null)
        .Select(value => value!)
        .OrderBy(value => value.ExitAtUtc)
        .ThenBy(value => value.Symbol, StringComparer.Ordinal).ToArray();
}
