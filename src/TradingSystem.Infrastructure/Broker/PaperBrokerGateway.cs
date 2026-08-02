using System.Globalization;
using System.Text.Json;
using TradingSystem.Application.Broker;
using TradingSystem.Domain;
using TradingSystem.Domain.Trading;

namespace TradingSystem.Infrastructure.Broker;

internal sealed class PaperBrokerGateway(
    PaperBrokerStateStore store,
    IPaperBrokerJournal journal,
    TimeProvider timeProvider) : IBrokerGateway, IPaperBrokerControl
{
    private const string SubmittedEvent = "OrderSubmitted";
    private const string FilledEvent = "OrderFilled";
    private const string CancelledEvent = "OrderCancelled";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public TradingMode Mode => TradingMode.Paper;

    public async Task<BrokerOrderSnapshot> SubmitAsync(
        BrokerOrderRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Validate(request);
        await store.Gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureRestoredAsync(cancellationToken);
            if (store.Orders.TryGetValue(request.ClientReference, out var existing))
            {
                if (!HasSamePayload(existing, request))
                {
                    throw new DuplicateBrokerOrderException(
                        $"Client reference '{request.ClientReference}' was reused with a different payload.");
                }

                return ToSnapshot(existing);
            }

            var orderSequence = checked(store.NextOrderSequence + 1);
            var now = timeProvider.GetUtcNow();
            var state = new PaperOrderState
            {
                ClientReference = request.ClientReference,
                BrokerOrderId = $"PAPER-{orderSequence:D10}",
                InstrumentId = request.InstrumentId,
                Direction = request.Direction,
                RequestedQuantity = request.Quantity,
                ExecutionPrice = request.ExecutionPrice,
                MaximumFillQuantityPerCycle = Math.Min(
                    request.MaximumFillQuantityPerCycle,
                    request.Quantity),
                State = OrderState.BrokerAcknowledged,
                UpdatedAtUtc = now
            };
            var payload = new SubmittedPayload(
                orderSequence,
                state.BrokerOrderId,
                state.InstrumentId,
                state.Direction,
                state.RequestedQuantity,
                state.ExecutionPrice,
                state.MaximumFillQuantityPerCycle,
                state.UpdatedAtUtc);
            await AppendAsync(SubmittedEvent, state.ClientReference, payload, now, cancellationToken);
            store.NextOrderSequence = orderSequence;
            store.Orders.Add(state.ClientReference, state);
            return ToSnapshot(state);
        }
        finally
        {
            store.Gate.Release();
        }
    }

    public async Task<BrokerOrderSnapshot?> GetOrderAsync(
        string clientReference,
        CancellationToken cancellationToken)
    {
        await store.Gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureRestoredAsync(cancellationToken);
            return store.Orders.TryGetValue(clientReference, out var order)
                ? ToSnapshot(order)
                : null;
        }
        finally
        {
            store.Gate.Release();
        }
    }

    public async Task<BrokerOrderSnapshot> CancelAsync(
        string clientReference,
        CancellationToken cancellationToken)
    {
        await store.Gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureRestoredAsync(cancellationToken);
            var order = GetRequiredOrder(clientReference);
            if (order.State == OrderState.Cancelled)
            {
                return ToSnapshot(order);
            }

            if (order.State is not (OrderState.BrokerAcknowledged or OrderState.PartiallyFilled))
            {
                throw new InvalidOperationException($"Order in state {order.State} cannot be cancelled.");
            }

            var now = timeProvider.GetUtcNow();
            await AppendAsync(CancelledEvent, clientReference, new CancelledPayload(now), now,
                cancellationToken);
            order.State = OrderState.Cancelled;
            order.UpdatedAtUtc = now;
            return ToSnapshot(order);
        }
        finally
        {
            store.Gate.Release();
        }
    }

    public async Task<BrokerOrderSnapshot> ProcessNextFillAsync(
        string clientReference,
        CancellationToken cancellationToken)
    {
        await store.Gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureRestoredAsync(cancellationToken);
            var order = GetRequiredOrder(clientReference);
            if (order.State is not (OrderState.BrokerAcknowledged or OrderState.PartiallyFilled))
            {
                throw new InvalidOperationException($"Order in state {order.State} cannot be filled.");
            }

            var remaining = order.RequestedQuantity - order.FilledQuantity;
            var fillQuantity = Math.Min(remaining, order.MaximumFillQuantityPerCycle);
            var cumulativeQuantity = order.FilledQuantity + fillQuantity;
            var nextState = cumulativeQuantity == order.RequestedQuantity
                ? OrderState.Filled
                : OrderState.PartiallyFilled;
            var now = timeProvider.GetUtcNow();
            var payload = new FilledPayload(cumulativeQuantity, order.ExecutionPrice, nextState, now);
            await AppendAsync(FilledEvent, clientReference, payload, now, cancellationToken);
            ApplyFill(order, payload);
            return ToSnapshot(order);
        }
        finally
        {
            store.Gate.Release();
        }
    }

    public async Task<IReadOnlyList<BrokerPositionSnapshot>> GetPositionsAsync(
        CancellationToken cancellationToken)
    {
        await store.Gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureRestoredAsync(cancellationToken);
            return store.Positions.Values
                .Where(position => position.Quantity > 0)
                .OrderBy(position => position.InstrumentId)
                .ThenBy(position => position.Direction)
                .Select(position => new BrokerPositionSnapshot(
                    position.InstrumentId,
                    position.Direction,
                    position.Quantity,
                    position.AverageEntryPrice))
                .ToArray();
        }
        finally
        {
            store.Gate.Release();
        }
    }

    public async Task<BrokerReconciliationResult> ReconcileAsync(
        IReadOnlyCollection<ExpectedBrokerPosition> expectedPositions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expectedPositions);
        await store.Gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureRestoredAsync(cancellationToken);
            var actual = store.Positions.Values
                .Where(position => position.Quantity > 0)
                .ToDictionary(
                    position => (position.InstrumentId, position.Direction),
                    position => position.Quantity);
            var expected = expectedPositions
                .GroupBy(position => (position.InstrumentId, position.Direction))
                .ToDictionary(group => group.Key, group => group.Sum(value => value.Quantity));
            var keys = actual.Keys.Union(expected.Keys)
                .OrderBy(key => key.InstrumentId)
                .ThenBy(key => key.Direction);
            var mismatches = new List<string>();

            foreach (var key in keys)
            {
                actual.TryGetValue(key, out var actualQuantity);
                expected.TryGetValue(key, out var expectedQuantity);
                if (actualQuantity != expectedQuantity)
                {
                    mismatches.Add(
                        $"{key.InstrumentId:N}/{key.Direction}: expected {expectedQuantity}, actual {actualQuantity}.");
                }
            }

            return new BrokerReconciliationResult(
                mismatches.Count == 0,
                mismatches.Count == 0,
                mismatches,
                timeProvider.GetUtcNow());
        }
        finally
        {
            store.Gate.Release();
        }
    }

    private async Task EnsureRestoredAsync(CancellationToken cancellationToken)
    {
        if (store.IsRestored)
        {
            return;
        }

        var entries = await journal.ReadAllAsync(cancellationToken);
        store.Orders.Clear();
        store.Positions.Clear();
        store.NextEventSequence = 0;
        store.NextOrderSequence = 0;
        long expectedSequence = 1;
        foreach (var entry in entries)
        {
            if (entry.Sequence != expectedSequence)
            {
                throw new InvalidOperationException(
                    $"Paper broker journal sequence is invalid. Expected {expectedSequence}, found {entry.Sequence}.");
            }

            Replay(entry);
            store.NextEventSequence = entry.Sequence;
            expectedSequence++;
        }

        store.IsRestored = true;
    }

    private void Replay(PaperBrokerJournalEntry entry)
    {
        switch (entry.EventType)
        {
            case SubmittedEvent:
                var submitted = Deserialize<SubmittedPayload>(entry);
                if (store.Orders.ContainsKey(entry.ClientReference))
                {
                    throw CorruptJournal(entry, "duplicate order submission");
                }

                store.Orders.Add(entry.ClientReference, new PaperOrderState
                {
                    ClientReference = entry.ClientReference,
                    BrokerOrderId = submitted.BrokerOrderId,
                    InstrumentId = submitted.InstrumentId,
                    Direction = submitted.Direction,
                    RequestedQuantity = submitted.RequestedQuantity,
                    ExecutionPrice = submitted.ExecutionPrice,
                    MaximumFillQuantityPerCycle = submitted.MaximumFillQuantityPerCycle,
                    State = OrderState.BrokerAcknowledged,
                    UpdatedAtUtc = submitted.UpdatedAtUtc
                });
                store.NextOrderSequence = Math.Max(store.NextOrderSequence, submitted.OrderSequence);
                break;
            case FilledEvent:
                ApplyFill(GetReplayOrder(entry), Deserialize<FilledPayload>(entry));
                break;
            case CancelledEvent:
                var cancelled = Deserialize<CancelledPayload>(entry);
                var cancelledOrder = GetReplayOrder(entry);
                if (cancelledOrder.State is not (OrderState.BrokerAcknowledged or OrderState.PartiallyFilled))
                {
                    throw CorruptJournal(entry, $"cannot cancel state {cancelledOrder.State}");
                }

                cancelledOrder.State = OrderState.Cancelled;
                cancelledOrder.UpdatedAtUtc = cancelled.UpdatedAtUtc;
                break;
            default:
                throw CorruptJournal(entry, $"unknown event type '{entry.EventType}'");
        }
    }

    private void ApplyFill(PaperOrderState order, FilledPayload payload)
    {
        if (order.State is not (OrderState.BrokerAcknowledged or OrderState.PartiallyFilled) ||
            payload.CumulativeFilledQuantity <= order.FilledQuantity ||
            payload.CumulativeFilledQuantity > order.RequestedQuantity)
        {
            throw new InvalidOperationException("Paper broker fill event is inconsistent with order state.");
        }

        var fillQuantity = payload.CumulativeFilledQuantity - order.FilledQuantity;
        order.FilledQuantity = payload.CumulativeFilledQuantity;
        order.AverageFillPrice = payload.AverageFillPrice;
        order.State = payload.State;
        order.UpdatedAtUtc = payload.UpdatedAtUtc;
        ApplyFillToPosition(order, fillQuantity);
    }

    private async Task AppendAsync<TPayload>(string eventType, string clientReference,
        TPayload payload, DateTimeOffset occurredAtUtc, CancellationToken cancellationToken)
    {
        var sequence = checked(store.NextEventSequence + 1);
        var entry = new PaperBrokerJournalEntry(
            Guid.NewGuid(),
            sequence,
            eventType,
            clientReference,
            JsonSerializer.Serialize(payload, JsonOptions),
            occurredAtUtc);
        try
        {
            await journal.AppendAsync(entry, cancellationToken);
        }
        catch
        {
            // A cancelled or failed database call can have an unknown commit outcome.
            // Force authoritative replay before any subsequent broker operation.
            store.IsRestored = false;
            throw;
        }

        store.NextEventSequence = sequence;
    }

    private void ApplyFillToPosition(PaperOrderState order, int fillQuantity)
    {
        var oppositeDirection = order.Direction == Direction.Buy ? Direction.Sell : Direction.Buy;
        var oppositeKey = (order.InstrumentId, oppositeDirection);
        if (store.Positions.TryGetValue(oppositeKey, out var oppositePosition))
        {
            var offsetQuantity = Math.Min(fillQuantity, oppositePosition.Quantity);
            oppositePosition.Quantity -= offsetQuantity;
            fillQuantity -= offsetQuantity;
            if (oppositePosition.Quantity == 0)
            {
                store.Positions.Remove(oppositeKey);
            }
        }

        if (fillQuantity == 0)
        {
            return;
        }

        var key = (order.InstrumentId, order.Direction);
        if (!store.Positions.TryGetValue(key, out var position))
        {
            position = new PaperPositionState
            {
                InstrumentId = order.InstrumentId,
                Direction = order.Direction
            };
            store.Positions.Add(key, position);
        }

        var previousValue = position.AverageEntryPrice * position.Quantity;
        position.Quantity += fillQuantity;
        position.AverageEntryPrice =
            (previousValue + (order.ExecutionPrice * fillQuantity)) / position.Quantity;
    }

    private PaperOrderState GetRequiredOrder(string clientReference) =>
        store.Orders.TryGetValue(clientReference, out var order)
            ? order
            : throw new BrokerOrderNotFoundException(clientReference);

    private PaperOrderState GetReplayOrder(PaperBrokerJournalEntry entry) =>
        store.Orders.TryGetValue(entry.ClientReference, out var order)
            ? order
            : throw CorruptJournal(entry, "event references an unknown order");

    private static TPayload Deserialize<TPayload>(PaperBrokerJournalEntry entry) =>
        JsonSerializer.Deserialize<TPayload>(entry.PayloadJson, JsonOptions)
        ?? throw CorruptJournal(entry, "payload is empty or invalid");

    private static InvalidOperationException CorruptJournal(
        PaperBrokerJournalEntry entry,
        string reason) => new(
        $"Paper broker journal event {entry.Sequence.ToString(CultureInfo.InvariantCulture)} is invalid: {reason}.");

    private static void Validate(BrokerOrderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ClientReference) || request.ClientReference.Length > 80)
        {
            throw new ArgumentException("Client reference must contain 1 to 80 characters.", nameof(request));
        }

        if (request.InstrumentId == Guid.Empty || request.Quantity <= 0 ||
            request.ExecutionPrice <= 0 || request.MaximumFillQuantityPerCycle <= 0)
        {
            throw new ArgumentException("Instrument, quantity, price, and fill size must be positive.", nameof(request));
        }
    }

    private static bool HasSamePayload(PaperOrderState existing, BrokerOrderRequest request) =>
        existing.InstrumentId == request.InstrumentId &&
        existing.Direction == request.Direction &&
        existing.RequestedQuantity == request.Quantity &&
        existing.ExecutionPrice == request.ExecutionPrice &&
        existing.MaximumFillQuantityPerCycle == Math.Min(request.MaximumFillQuantityPerCycle, request.Quantity);

    private static BrokerOrderSnapshot ToSnapshot(PaperOrderState order) => new(
        order.ClientReference,
        order.BrokerOrderId,
        order.InstrumentId,
        order.Direction,
        order.RequestedQuantity,
        order.FilledQuantity,
        order.AverageFillPrice,
        order.State,
        order.UpdatedAtUtc);

    private sealed record SubmittedPayload(
        long OrderSequence,
        string BrokerOrderId,
        Guid InstrumentId,
        Direction Direction,
        int RequestedQuantity,
        decimal ExecutionPrice,
        int MaximumFillQuantityPerCycle,
        DateTimeOffset UpdatedAtUtc);

    private sealed record FilledPayload(
        int CumulativeFilledQuantity,
        decimal AverageFillPrice,
        OrderState State,
        DateTimeOffset UpdatedAtUtc);

    private sealed record CancelledPayload(DateTimeOffset UpdatedAtUtc);
}
