using System.Text.Json;
using TradingSystem.Application.Broker;
using TradingSystem.Domain.Trading;

namespace TradingSystem.Infrastructure.Execution;

// No paper writes, no replay inputs and NO buy/sell submission. The only broker
// mutations are cancellation/resizing of this intent's positively identified SL.
internal static class LiveExternalExitReconciler
{
    internal sealed record Assessment(bool Changed, bool Safe, string Reason, int Remaining,
        int TotalExternal, IReadOnlyList<LiveAccountOrder> ExternalOrders);

    internal static Assessment Assess(LiveExecutionIntent intent, string symbol, string exchange,
        LiveAccountSnapshot snapshot, int owners, IReadOnlySet<string> managedReferences, int? aggregateManagedQuantity = null)
    {
        Assessment Block(string why) => new(true, false, why, 0, 0, []);
        var orders = snapshot.Orders.Where(x => x.Symbol == symbol && x.Exchange == exchange &&
            x.Product == "NRML" && x.CreatedAt >= intent.CreatedAtUtc).ToArray();
        var held = snapshot.Positions.Where(x => x.Symbol == symbol && x.Exchange == exchange && x.Product == "NRML")
            .Sum(x => x.Quantity);
        var external = orders.Where(x => x.Direction == Direction.Sell && !managedReferences.Contains(x.Reference) &&
            x.Id != intent.ProtectionId && x.Id != intent.BrokerOrderId && x.Filled > 0).ToArray();
        var waitingSell = orders.Any(x => x.Direction == Direction.Sell && !managedReferences.Contains(x.Reference) &&
            x.Id != intent.ProtectionId && x.Filled == 0 &&
            x.State is not (OrderState.Cancelled or OrderState.RejectedByBroker or OrderState.Filled));
        if (waitingSell) return Block("External sell is pending; hold automated sells and scaling until its outcome is known.");
        var total = external.Sum(x => x.Filled);
        var ownedStop = snapshot.Orders.SingleOrDefault(x => x.Id == intent.ProtectionId);
        var expectedHeld = aggregateManagedQuantity ?? intent.FilledQuantity;
        if (total == intent.ExternalExitQuantity && !intent.ExternalReconciliationPending &&
            held == expectedHeld - intent.FilledQuantity &&
            ownedStop is { State: OrderState.Filled } && ownedStop.Filled == intent.FilledQuantity &&
            managedReferences.Contains(ownedStop.Reference))
            return new(false, true, "Owned stop fill is handled by normal reconciliation.", 0, 0, []);
        var changed = total != intent.ExternalExitQuantity || intent.ExternalReconciliationPending || held != expectedHeld;
        if (!changed) return new(false, true, "No external exposure change.", held, total, external);
        if (owners != 1) return Block("Same-contract ownership is ambiguous; no fill attribution or protection change.");
        if (intent.Direction != Direction.Buy || intent.Status is not ("Protected" or "ProtectedScaleMismatch" or "FinalExitPending"))
            return Block("An in-flight lifecycle action needs reconciliation before external attribution.");
        if (orders.Any(x => managedReferences.Contains(x.Reference) && x.Id != intent.ProtectionId &&
            x.Direction == Direction.Sell && x.State is not (OrderState.Filled or OrderState.Cancelled or OrderState.RejectedByBroker)))
            return Block("A system sell is still in flight; do not attribute or release its external-exit hold.");
        if (held < 0) return Block("Broker is short after competing exits; no automatic corrective buy is permitted.");
        if (external.Any(x => x.State is not (OrderState.Filled or OrderState.Cancelled or OrderState.PartiallyFilled or OrderState.BrokerAcknowledged)))
            return Block("External sell state is uncertain.");
        if (orders.Any(x => x.Direction == Direction.Buy && !managedReferences.Contains(x.Reference)))
            return Block("Unmanaged buy shares this contract; attribution is ambiguous.");
        if (total < intent.ExternalExitQuantity || total - intent.ExternalExitQuantity > intent.FilledQuantity ||
            intent.FilledQuantity - (total - intent.ExternalExitQuantity) != held)
            return Block("External fills and broker exposure do not balance; possible competing stop or delayed snapshot.");
        var stop = snapshot.Orders.SingleOrDefault(x => x.Id == intent.ProtectionId);
        if (stop is null || !managedReferences.Contains(stop.Reference) || stop.Symbol != symbol ||
            stop.Exchange != exchange || stop.Product != "NRML" || stop.Direction != Direction.Sell || stop.Filled != 0)
            return Block("Managed stop identity/fills are uncertain; do not touch another order.");
        if (stop.State is not (OrderState.BrokerAcknowledged or OrderState.Cancelled))
            return Block("Managed stop is not in a confirmed cancellable state.");
        if (held > 0 && stop.State == OrderState.Cancelled)
            return Block("Partial external exit has no working stop; requires protection recovery.");
        return new(true, true, "Confirmed external exit.", held, total, external);
    }

    internal static async Task<bool> ReconcileAsync(LiveExecutionIntent intent, string symbol, string exchange,
        LiveAccountSnapshot snapshot, int owners, IReadOnlySet<string> references,
        ILiveAccountEvidenceGateway broker, Func<CancellationToken, Task> persist, CancellationToken token,
        int? aggregateManagedQuantity = null,
        Func<BrokerProtectionSnapshot?, CancellationToken, Task>? recordConfirmation = null)
    {
        var plan = Assess(intent, symbol, exchange, snapshot, owners, references, aggregateManagedQuantity);
        if (!plan.Changed) return true;
        intent.BeginExternalReconciliation();
        await persist(token); // Durable hold BEFORE an uncertain broker mutation.
        if (!plan.Safe) { intent.RecordExitUncertainty(plan.Reason); await persist(token); return false; }
        // Persisted old quantities stay unchanged until stop and actual fills are
        // confirmed. Repeating after a crash is idempotent, including cancelled SL.
        var stop = snapshot.Orders.Single(x => x.Id == intent.ProtectionId);
        if (plan.Remaining == 0)
        {
            if (stop.State != OrderState.Cancelled) await broker.CancelProtectionAsync(stop.Id, token);
        }
        else if (stop.Quantity != plan.Remaining || stop.Trigger != intent.StopLoss)
            await broker.ModifyProtectionAsync(stop.Id, plan.Remaining, intent.StopLoss, token);

        // A successful cancel response is NOT evidence that no stop fill raced it.
        var confirmed = await broker.ReadAccountAsync(token);
        var check = Assess(intent, symbol, exchange, confirmed, owners, references);
        var confirmedStop = confirmed.Orders.SingleOrDefault(x => x.Id == intent.ProtectionId);
        if (!check.Safe || check.Remaining != plan.Remaining || check.TotalExternal != plan.TotalExternal ||
            !check.ExternalOrders.OrderBy(x => x.Id).SequenceEqual(plan.ExternalOrders.OrderBy(x => x.Id)) ||
            confirmedStop is null || confirmedStop.Filled != 0 ||
            (plan.Remaining == 0 ? confirmedStop.State != OrderState.Cancelled :
                confirmedStop.State != OrderState.BrokerAcknowledged || confirmedStop.Quantity != plan.Remaining ||
                confirmedStop.Trigger != intent.StopLoss))
        {
            intent.RecordExitUncertainty("Broker changed during external-exit reconciliation; automated actions remain held.");
            await persist(token);
            return false;
        }
        if (check.ExternalOrders.Any(x => x.State is not (OrderState.Filled or OrderState.Cancelled)))
        {
            intent.RecordExitUncertainty("External sell is still working; stop resized to confirmed remainder, automated actions held.");
            await persist(token);
            return false;
        }
        // Protect exposure before waiting for the trade-price endpoint. A lagging
        // fill ledger must not leave an oversized sell stop behind a flat account.
        var fills = new List<ExternalExitFill>();
        foreach (var order in check.ExternalOrders)
            fills.Add(await broker.ReadExternalFillAsync(order.Id, order.Filled, token));
        intent.CompleteExternalReconciliation(plan.TotalExternal, plan.Remaining,
            JsonSerializer.Serialize(fills), confirmed.ObservedAt);
        await persist(token);
        if (recordConfirmation is not null)
            await recordConfirmation(plan.Remaining == 0 ? null : new BrokerProtectionSnapshot(
                confirmedStop.Reference, confirmedStop.Id, "OPEN", confirmedStop.Quantity,
                confirmedStop.Trigger!.Value, confirmed.ObservedAt), token);
        return true;
    }
}
