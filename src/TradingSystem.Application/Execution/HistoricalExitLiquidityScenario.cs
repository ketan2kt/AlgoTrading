namespace TradingSystem.Application.Execution;

public sealed record HistoricalExitLiquidityFill(DateTimeOffset AtUtc, DateTimeOffset BookAtUtc,
    int Units, decimal ObservedBid, decimal ModeledPrice);

public sealed record HistoricalExitLiquidityResult(string Symbol, DateTimeOffset TriggerAtUtc,
    DateTimeOffset DeadlineUtc, string ExitReason, int RequestedUnits, int ModeledUnits,
    decimal? AverageModeledPrice, decimal? ModeledNetPnl, IReadOnlyList<HistoricalExitLiquidityFill> Fills);

// This diagnostic never mutates portfolio state or contributes to verified P&L.
// Later snapshots don't prove replenishment. Use only increased displayed size
// at a previously used price, never reuse a book timestamp, and charge each
// modeled child as a separate order. No queue priority is inferred.
public static class HistoricalExitLiquidityScenario
{
    internal static HistoricalExitLiquidityResult? Evaluate(HistoricalReplayEntryEvidence entry,
        HistoricalReplayEntryEvidence exit, IReadOnlyList<HistoricalReplayQuote> books,
        string exchangeCode)
    {
        if (entry.Stage != "Entry filled" || exit.Stage != "Exit unsupported" ||
            exit.ExitTriggerAtUtc is not { } trigger || exit.ExitDeadlineUtc is not { } deadline ||
            exit.ExitReason is not { } reason || entry.ModeledPrice is not > 0m || entry.LotSize is not > 0 ||
            entry.Quantity != exit.Quantity || entry.Quantity <= 0 || entry.Quantity % entry.LotSize.Value != 0 ||
            !string.Equals(entry.Symbol, exit.Symbol, StringComparison.Ordinal) || entry.DecisionAtUtc != exit.DecisionAtUtc ||
            entry.AtUtc >= trigger || deadline <= trigger) return null;

        var remaining = exit.Quantity;
        var previous = trigger;
        var lastBook = trigger;
        var usedAtPrice = new Dictionary<decimal, int>();
        var fills = new List<HistoricalExitLiquidityFill>();
        // Paths here are in receipt order and already vetted for quote and depth freshness.
        var low = 0;
        var high = books.Count;
        while (low < high)
        {
            var mid = low + (high - low) / 2;
            if (books[mid].AvailableAtUtc <= trigger) low = mid + 1;
            else high = mid;
        }
        for (var i = low; i < books.Count && books[i].AvailableAtUtc <= deadline; i++)
        {
            var quote = books[i];
            var at = quote.AvailableAtUtc;
            if (at - previous > TimeSpan.FromSeconds(30)) break;
            previous = at;
            if (quote.DepthSourceAtUtc is not { } depth || depth <= lastBook || depth > at ||
                !quote.HasExecutableBook() || quote.BidPrice is not { } bid || quote.BidQuantity is not > 0) continue;
            lastBook = depth;
            var used = usedAtPrice.GetValueOrDefault(bid);
            var offered = Math.Min(remaining, Math.Max(0L, quote.BidQuantity.Value - used));
            var units = (int)(offered / entry.LotSize.Value) * entry.LotSize.Value;
            if (units == 0) continue;
            usedAtPrice[bid] = used + units;
            fills.Add(new(at, depth, units, bid, Math.Max(.01m,
                bid * (1m - SessionPolicyReceipt.ComparisonExecution.ExitSlippagePercent))));
            remaining -= units;
            if (remaining == 0) break;
        }
        var filled = exit.Quantity - remaining;
        var turnover = fills.Sum(fill => fill.ModeledPrice * fill.Units);
        decimal? net = null;
        if (remaining == 0)
        {
            var buy = entry.ModeledPrice.Value * entry.Quantity;
            var costs = PaperTradingCostModel.CalculateOptionCharges([
                new(PaperOptionTransactionSide.Buy, entry.ModeledPrice.Value, entry.Quantity),
                .. fills.Select(fill => new PaperOptionTransaction(PaperOptionTransactionSide.Sell, fill.ModeledPrice, fill.Units))
            ], exchangeCode).Total;
            net = turnover - buy - costs;
        }
        return new(entry.Symbol, trigger, deadline, reason, exit.Quantity, filled,
            filled > 0 ? turnover / filled : null, net, fills);
    }
}
