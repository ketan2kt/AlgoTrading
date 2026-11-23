using TradingSystem.Application.Execution;
using TradingSystem.Application.MarketData;
using TradingSystem.Domain.Trading;

namespace TradingSystem.Infrastructure.MarketData;

// Display-only estimate at the last market mark. Stored results and execution are untouched.
internal static class WorkspaceOpenPnl
{
    internal static WorkspacePnlEstimate? FromPosition(MarketPaperPosition position, string exchange)
    {
        if (position.Status != "Active" || position.CurrentPrice <= 0 || position.RemainingQuantity <= 0)
            return null;
        var buy = position.Direction == Direction.Buy;
        var remainingCosts = PaperTradingCostModel.CalculateOptionCharges([
            new(buy ? PaperOptionTransactionSide.Buy : PaperOptionTransactionSide.Sell,
                position.EntryPrice, position.RemainingQuantity),
            new(buy ? PaperOptionTransactionSide.Sell : PaperOptionTransactionSide.Buy,
                position.CurrentPrice, position.RemainingQuantity)], exchange).Total;
        var gross = position.CurrentPnlAt(position.CurrentPrice) + position.CostsPaid;
        var costs = position.CostsPaid + remainingCosts;
        return new(gross, costs, gross - costs);
    }

    internal static WorkspacePnlEstimate? FromJournal(Guid signalId, Direction direction,
        decimal? mark, IReadOnlyDictionary<string, string> filledOrders)
    {
        if (mark is not > 0 || !filledOrders.TryGetValue($"{signalId:N}-ENTRY", out var entryPayload))
            return null;
        var entry = WorkspaceExitBreakdownBuilder.FromJournal("Entry", entryPayload);
        if (entry is null) return null;
        var entries = new List<WorkspaceExitFill> { entry };
        if (filledOrders.TryGetValue($"{signalId:N}-ADDON", out var addOnPayload))
        {
            var addOn = WorkspaceExitBreakdownBuilder.FromJournal("Add-on", addOnPayload);
            if (addOn is null) return null;
            entries.Add(addOn);
        }
        WorkspaceExitFill? partial = null;
        if (filledOrders.TryGetValue($"{signalId:N}-PARTIAL", out var partialPayload))
        {
            partial = WorkspaceExitBreakdownBuilder.FromJournal("Partial", partialPayload);
            if (partial is null) return null;
        }
        var remaining = entries.Sum(x => x.Quantity) - (partial?.Quantity ?? 0);
        if (remaining <= 0) return null;
        var buy = direction == Direction.Buy;
        var entrySide = buy ? PaperOptionTransactionSide.Buy : PaperOptionTransactionSide.Sell;
        var exitSide = buy ? PaperOptionTransactionSide.Sell : PaperOptionTransactionSide.Buy;
        var transactions = entries.Select(x => new PaperOptionTransaction(entrySide, x.Price, x.Quantity)).ToList();
        if (partial is not null) transactions.Add(new(exitSide, partial.Price, partial.Quantity));
        transactions.Add(new(exitSide, mark.Value, remaining));
        var gross = ((partial?.Price * partial?.Quantity ?? 0m) + mark.Value * remaining -
            entries.Sum(x => x.Price * x.Quantity)) * (buy ? 1 : -1);
        var costs = PaperTradingCostModel.CalculateOptionCharges(transactions).Total;
        return new(gross, costs, gross - costs);
    }
}
