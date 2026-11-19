using TradingSystem.Application.MarketData;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Diagnostics;

namespace TradingSystem.Infrastructure.MarketData;

// Read-only presentation: never changes stored exit prices, charges or P&L.
internal static class WorkspaceExitBreakdownBuilder
{
    internal static WorkspaceExitFill? FromJournal(string kind, string payload)
    {
        var fill = LiveFillPnl.ReadPaperLeg(payload);
        return fill is null ? null : new(kind, fill.Qty, fill.Price);
    }

    internal static WorkspaceExitBreakdown Build(int quantity, decimal entry, decimal gross,
        Direction direction, IEnumerable<WorkspaceExitFill> source)
    {
        var fills = source.ToArray();
        var valid = quantity > 0 && entry > 0 && fills.Length > 0 &&
            fills.All(f => f.Quantity > 0 && f.Price > 0 && f.Kind is "Partial" or "Final") &&
            fills.Count(f => f.Kind == "Final") == 1 && fills[^1].Kind == "Final" &&
            fills.Sum(f => (long)f.Quantity) == quantity;
        if (!valid) return new(null, false, fills);
        var proceeds = fills.Sum(f => f.Price * f.Quantity);
        var reconstructedGross = (proceeds - entry * quantity) * (direction == Direction.Buy ? 1 : -1);
        // Same rounding tolerance as the existing recorded-fill reconciliation.
        if (Math.Abs(reconstructedGross - gross) > 0.1m) return new(null, false, fills);
        return new(proceeds / quantity, true, fills);
    }

    internal static WorkspaceExitBreakdown FromPosition(MarketPaperPosition position)
    {
        var fills = new List<WorkspaceExitFill>();
        if (position.PartialExitQuantity > 0 && position.PartialExitPrice is { } partial)
            fills.Add(new("Partial", position.PartialExitQuantity, partial));
        fills.Add(new("Final", position.Quantity - position.PartialExitQuantity, position.CurrentPrice));
        return Build(position.Quantity, position.EntryPrice, position.RealisedPnl + position.CostsPaid,
            position.Direction, fills);
    }
}
