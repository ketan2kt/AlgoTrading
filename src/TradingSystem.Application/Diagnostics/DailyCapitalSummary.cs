namespace TradingSystem.Application.Diagnostics;

public sealed record DailyCapitalSummary(decimal NetRealisedPnl, decimal? PeakCapitalUsed,
    DateTimeOffset? PeakAtUtc, decimal? ReturnPercent, string Status, string Note);

public sealed record DailyCapitalTrade(DailyTradingTrade Trade,
    IReadOnlyList<PaperTimelineEvent> Events, bool EvidenceComplete = true);

/// <summary>Concurrent long-option premium cost, not turnover, market value or broker margin.</summary>
public static class DailyCapitalSummaryBuilder
{
    public static DailyCapitalSummary Build(IReadOnlyList<DailyCapitalTrade> positions,
        bool missingEntries = false)
    {
        var net = positions.Where(p => p.Trade.ClosedAtUtc.HasValue).Sum(p => p.Trade.NetRealisedPnl);
        DailyCapitalSummary Missing() => new(net, null, null, null, "Incomplete",
            "Peak capital unavailable: entry, scale-in or partial-exit fill evidence is incomplete.");
        if (missingEntries || positions.Select(p => p.Trade.TradeId).Distinct().Count() != positions.Count)
            return Missing();

        var changes = new List<(DateTimeOffset At, decimal Amount)>();
        foreach (var position in positions)
        {
            var trade = position.Trade;
            var events = position.Events.Where(e => e.Kind != "Stop").OrderBy(e => e.At)
                .ThenBy(e => e.Kind is "Entry" or "Add-on" ? 0 : 1).ToArray();
            if (!position.EvidenceComplete || trade.Quantity <= 0 || trade.EntryPrice <= 0 ||
                events.Length == 0 || events[0].Kind != "Entry" || events.Count(e => e.Kind == "Entry") != 1)
                return Missing();

            int held = 0, bought = 0;
            decimal cost = 0, purchases = 0, proceeds = 0;
            bool exited = false;
            foreach (var e in events)
            {
                if (exited || e.Units <= 0 || e.Price <= 0 || e.At < trade.OpenedAtUtc ||
                    (trade.ClosedAtUtc is { } closed && e.At > closed)) return Missing();
                var before = cost;
                switch (e.Kind)
                {
                    case "Entry":
                    case "Add-on":
                        cost += e.Units * e.Price;
                        purchases += e.Units * e.Price;
                        held += e.Units;
                        bought += e.Units;
                        break;
                    case "Partial":
                    case "Exit":
                        if (held < e.Units || held == 0) return Missing();
                        cost -= cost / held * e.Units;
                        held -= e.Units;
                        if (held == 0) cost = 0;
                        proceeds += e.Units * e.Price;
                        exited = e.Kind == "Exit";
                        // Nifty saves its result after the final fill; the receipt is the capital-release clock.
                        if (exited && (held != 0 || !trade.ClosedAtUtc.HasValue)) return Missing();
                        break;
                    default:
                        return Missing();
                }
                changes.Add((e.At, cost - before));
            }
            if (bought != trade.Quantity || Math.Abs(purchases - trade.EntryPrice * bought) > 0.00005m * bought + 0.01m ||
                (trade.ClosedAtUtc.HasValue && (!exited || held != 0 ||
                    Math.Abs(proceeds - purchases - trade.GrossPnl) > 0.1m)) ||
                (!trade.ClosedAtUtc.HasValue && (exited || held == 0))) return Missing();
        }

        decimal current = 0, peak = 0;
        DateTimeOffset? peakAt = null;
        // Apply coincident receipts together: don't invent an overlap from arbitrary list ordering.
        foreach (var group in changes.GroupBy(c => c.At).OrderBy(g => g.Key))
        {
            current += group.Sum(c => c.Amount);
            if (current > peak) { peak = current; peakAt = group.Key; }
        }
        return new(net, decimal.Round(peak, 2), peakAt,
            peak > 0 ? decimal.Round(net / peak * 100, 4) : null,
            positions.Count == 0 ? "NoTrades" : "Verified",
            "Closed-trade net after costs, including Hero Zero. Peak is simultaneous open premium cost, " +
            "adjusted for scale-ins and partial exits; excludes fees and unrealised P&L. Not broker margin or turnover.");
    }
}
