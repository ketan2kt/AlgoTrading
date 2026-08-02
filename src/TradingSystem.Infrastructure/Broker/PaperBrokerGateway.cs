using TradingSystem.Application.Broker;
using TradingSystem.Domain;
using TradingSystem.Domain.Trading;

namespace TradingSystem.Infrastructure.Broker;

internal sealed class PaperBrokerGateway(
    PaperBrokerStateStore store,
    TimeProvider timeProvider) : IBrokerGateway, IPaperBrokerControl
{
    public TradingMode Mode => TradingMode.Paper;

    public Task<BrokerOrderSnapshot> SubmitAsync(
        BrokerOrderRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Validate(request);

        lock (store.SyncRoot)
        {
            if (store.Orders.TryGetValue(request.ClientReference, out var existing))
            {
                if (!HasSamePayload(existing, request))
                {
                    throw new DuplicateBrokerOrderException(
                        $"Client reference '{request.ClientReference}' was reused with a different payload.");
                }

                return Task.FromResult(ToSnapshot(existing));
            }

            var sequence = checked(++store.NextOrderSequence);
            var state = new PaperOrderState
            {
                ClientReference = request.ClientReference,
                BrokerOrderId = $"PAPER-{sequence:D10}",
                InstrumentId = request.InstrumentId,
                Direction = request.Direction,
                RequestedQuantity = request.Quantity,
                ExecutionPrice = request.ExecutionPrice,
                MaximumFillQuantityPerCycle = Math.Min(
                    request.MaximumFillQuantityPerCycle,
                    request.Quantity),
                State = OrderState.BrokerAcknowledged,
                UpdatedAtUtc = timeProvider.GetUtcNow()
            };
            store.Orders.Add(state.ClientReference, state);
            return Task.FromResult(ToSnapshot(state));
        }
    }

    public Task<BrokerOrderSnapshot?> GetOrderAsync(
        string clientReference,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (store.SyncRoot)
        {
            return Task.FromResult(
                store.Orders.TryGetValue(clientReference, out var order)
                    ? ToSnapshot(order)
                    : null);
        }
    }

    public Task<BrokerOrderSnapshot> CancelAsync(
        string clientReference,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (store.SyncRoot)
        {
            var order = GetRequiredOrder(clientReference);
            if (order.State == OrderState.Cancelled)
            {
                return Task.FromResult(ToSnapshot(order));
            }

            if (order.State is not (OrderState.BrokerAcknowledged or OrderState.PartiallyFilled))
            {
                throw new InvalidOperationException($"Order in state {order.State} cannot be cancelled.");
            }

            order.State = OrderState.Cancelled;
            order.UpdatedAtUtc = timeProvider.GetUtcNow();
            return Task.FromResult(ToSnapshot(order));
        }
    }

    public Task<BrokerOrderSnapshot> ProcessNextFillAsync(
        string clientReference,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (store.SyncRoot)
        {
            var order = GetRequiredOrder(clientReference);
            if (order.State is not (OrderState.BrokerAcknowledged or OrderState.PartiallyFilled))
            {
                throw new InvalidOperationException($"Order in state {order.State} cannot be filled.");
            }

            var remaining = order.RequestedQuantity - order.FilledQuantity;
            var fillQuantity = Math.Min(remaining, order.MaximumFillQuantityPerCycle);
            order.FilledQuantity += fillQuantity;
            order.AverageFillPrice = order.ExecutionPrice;
            order.State = order.FilledQuantity == order.RequestedQuantity
                ? OrderState.Filled
                : OrderState.PartiallyFilled;
            order.UpdatedAtUtc = timeProvider.GetUtcNow();
            ApplyFillToPosition(order, fillQuantity);
            return Task.FromResult(ToSnapshot(order));
        }
    }

    public Task<IReadOnlyList<BrokerPositionSnapshot>> GetPositionsAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (store.SyncRoot)
        {
            IReadOnlyList<BrokerPositionSnapshot> result = store.Positions.Values
                .Where(position => position.Quantity > 0)
                .OrderBy(position => position.InstrumentId)
                .ThenBy(position => position.Direction)
                .Select(position => new BrokerPositionSnapshot(
                    position.InstrumentId,
                    position.Direction,
                    position.Quantity,
                    position.AverageEntryPrice))
                .ToArray();
            return Task.FromResult(result);
        }
    }

    public Task<BrokerReconciliationResult> ReconcileAsync(
        IReadOnlyCollection<ExpectedBrokerPosition> expectedPositions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expectedPositions);
        cancellationToken.ThrowIfCancellationRequested();

        lock (store.SyncRoot)
        {
            var actual = store.Positions.Values
                .Where(position => position.Quantity > 0)
                .ToDictionary(
                    position => (position.InstrumentId, position.Direction),
                    position => position.Quantity);
            var expected = expectedPositions
                .GroupBy(position => (position.InstrumentId, position.Direction))
                .ToDictionary(group => group.Key, group => group.Sum(value => value.Quantity));
            var keys = actual.Keys.Union(expected.Keys).OrderBy(key => key.InstrumentId).ThenBy(key => key.Direction);
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

            return Task.FromResult(new BrokerReconciliationResult(
                mismatches.Count == 0,
                mismatches.Count == 0,
                mismatches,
                timeProvider.GetUtcNow()));
        }
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
}
