using TradingSystem.Domain.Common;

namespace TradingSystem.Domain.Trading;

public sealed class Signal : Entity, IAppendOnlyEntity
{
    public Signal(
        Guid id,
        Guid strategyVersionId,
        Guid instrumentId,
        Direction direction,
        decimal proposedEntry,
        decimal proposedStopLoss,
        decimal proposedTarget,
        decimal confidence,
        DateTimeOffset marketDataTimestampUtc,
        DateTimeOffset expiresAtUtc,
        string fingerprint,
        string reasonsJson) : base(id)
    {
        StrategyVersionId = strategyVersionId;
        InstrumentId = instrumentId;
        Direction = direction;
        ProposedEntry = proposedEntry;
        ProposedStopLoss = proposedStopLoss;
        ProposedTarget = proposedTarget;
        Confidence = confidence;
        MarketDataTimestampUtc = marketDataTimestampUtc.ToUniversalTime();
        ExpiresAtUtc = expiresAtUtc.ToUniversalTime();
        Fingerprint = fingerprint;
        ReasonsJson = reasonsJson;
    }

    public Guid StrategyVersionId { get; private init; }
    public Guid InstrumentId { get; private init; }
    public Direction Direction { get; private init; }
    public decimal ProposedEntry { get; private init; }
    public decimal ProposedStopLoss { get; private init; }
    public decimal ProposedTarget { get; private init; }
    public decimal Confidence { get; private init; }
    public DateTimeOffset MarketDataTimestampUtc { get; private init; }
    public DateTimeOffset ExpiresAtUtc { get; private init; }
    public string Fingerprint { get; private init; }
    public string ReasonsJson { get; private init; }
    public SignalStatus Status { get; private init; } = SignalStatus.Generated;
}

public sealed class RiskDecision : Entity, IAppendOnlyEntity
{
    public RiskDecision(
        Guid id,
        Guid signalId,
        bool approved,
        int approvedQuantity,
        string reasonsJson,
        string snapshotJson,
        DateTimeOffset decidedAtUtc) : base(id)
    {
        SignalId = signalId;
        Approved = approved;
        ApprovedQuantity = approvedQuantity;
        ReasonsJson = reasonsJson;
        SnapshotJson = snapshotJson;
        DecidedAtUtc = decidedAtUtc.ToUniversalTime();
    }

    public Guid SignalId { get; private init; }
    public bool Approved { get; private init; }
    public int ApprovedQuantity { get; private init; }
    public string ReasonsJson { get; private init; }
    public string SnapshotJson { get; private init; }
    public DateTimeOffset DecidedAtUtc { get; private init; }
}

public sealed class TradingOrder : MutableEntity
{
    public TradingOrder(
        Guid id,
        TradingMode mode,
        Guid riskDecisionId,
        string clientReference,
        int requestedQuantity,
        OrderState state,
        DateTimeOffset createdAtUtc) : base(id, createdAtUtc)
    {
        Mode = mode;
        RiskDecisionId = riskDecisionId;
        ClientReference = clientReference;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requestedQuantity);
        RequestedQuantity = requestedQuantity;
        State = state;
    }

    public TradingMode Mode { get; private init; }
    public Guid RiskDecisionId { get; private init; }
    public string ClientReference { get; private init; }
    public string? BrokerOrderId { get; private set; }
    public OrderState State { get; private set; }
    public int RequestedQuantity { get; private init; }
    public int FilledQuantity { get; private set; }
    public decimal? AverageFillPrice { get; private set; }

    public void Acknowledge(string brokerOrderId)
    {
        if (string.IsNullOrWhiteSpace(brokerOrderId))
        {
            throw new ArgumentException("Broker order identifier is required.", nameof(brokerOrderId));
        }

        TransitionTo(OrderState.BrokerAcknowledged);
        BrokerOrderId = brokerOrderId;
    }

    public void RecordFill(int cumulativeFilledQuantity, decimal averageFillPrice)
    {
        if (State is not (OrderState.BrokerAcknowledged or OrderState.PartiallyFilled))
        {
            throw new InvalidOperationException($"Cannot fill an order in state {State}.");
        }

        if (cumulativeFilledQuantity <= FilledQuantity || cumulativeFilledQuantity > RequestedQuantity)
        {
            throw new ArgumentOutOfRangeException(nameof(cumulativeFilledQuantity));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(averageFillPrice);
        FilledQuantity = cumulativeFilledQuantity;
        AverageFillPrice = averageFillPrice;
        State = FilledQuantity == RequestedQuantity ? OrderState.Filled : OrderState.PartiallyFilled;
    }

    public void RequestCancellation() => TransitionTo(OrderState.CancelPending);

    public void ConfirmCancellation() => TransitionTo(OrderState.Cancelled);

    public void RequireReconciliation()
    {
        if (State is OrderState.Cancelled or OrderState.Closed)
        {
            throw new InvalidOperationException($"Cannot reconcile a terminal order in state {State}.");
        }

        State = OrderState.ReconciliationRequired;
    }

    private void TransitionTo(OrderState next)
    {
        var valid = (State, next) switch
        {
            (OrderState.ReadyToSubmit, OrderState.BrokerAcknowledged) => true,
            (OrderState.BrokerAcknowledged, OrderState.CancelPending) => true,
            (OrderState.PartiallyFilled, OrderState.CancelPending) => true,
            (OrderState.CancelPending, OrderState.Cancelled) => true,
            _ => false
        };

        if (!valid)
        {
            throw new InvalidOperationException($"Invalid order transition {State} -> {next}.");
        }

        State = next;
    }
}

public sealed class OrderEvent : Entity, IAppendOnlyEntity
{
    public OrderEvent(
        Guid id,
        Guid orderId,
        OrderState previousState,
        OrderState newState,
        string reason,
        DateTimeOffset occurredAtUtc) : base(id)
    {
        OrderId = orderId;
        PreviousState = previousState;
        NewState = newState;
        Reason = reason;
        OccurredAtUtc = occurredAtUtc.ToUniversalTime();
    }

    public Guid OrderId { get; private init; }
    public OrderState PreviousState { get; private init; }
    public OrderState NewState { get; private init; }
    public string Reason { get; private init; }
    public DateTimeOffset OccurredAtUtc { get; private init; }
    public string? BrokerEvidenceJson { get; private init; }
}

public sealed class Trade : Entity, IAppendOnlyEntity
{
    public Trade(
        Guid id,
        Guid orderId,
        string brokerTradeId,
        int quantity,
        decimal price,
        DateTimeOffset executedAtUtc) : base(id)
    {
        OrderId = orderId;
        BrokerTradeId = brokerTradeId;
        Quantity = quantity;
        Price = price;
        ExecutedAtUtc = executedAtUtc.ToUniversalTime();
    }

    public Guid OrderId { get; private init; }
    public string BrokerTradeId { get; private init; }
    public int Quantity { get; private init; }
    public decimal Price { get; private init; }
    public DateTimeOffset ExecutedAtUtc { get; private init; }
}

public sealed class Position : MutableEntity
{
    public Position(
        Guid id,
        TradingMode mode,
        Guid instrumentId,
        Direction direction,
        PositionState state,
        DateTimeOffset createdAtUtc) : base(id, createdAtUtc)
    {
        Mode = mode;
        InstrumentId = instrumentId;
        Direction = direction;
        State = state;
    }

    public TradingMode Mode { get; private init; }
    public Guid InstrumentId { get; private init; }
    public Direction Direction { get; private init; }
    public PositionState State { get; private set; }
    public int Quantity { get; private set; }
    public decimal AverageEntryPrice { get; private set; }
    public decimal? StopLoss { get; private set; }
    public decimal? Target { get; private set; }
    public decimal RealisedPnl { get; private set; }
}

public sealed class PositionEvent : Entity, IAppendOnlyEntity
{
    public PositionEvent(
        Guid id,
        Guid positionId,
        PositionState previousState,
        PositionState newState,
        string reason,
        DateTimeOffset occurredAtUtc) : base(id)
    {
        PositionId = positionId;
        PreviousState = previousState;
        NewState = newState;
        Reason = reason;
        OccurredAtUtc = occurredAtUtc.ToUniversalTime();
    }

    public Guid PositionId { get; private init; }
    public PositionState PreviousState { get; private init; }
    public PositionState NewState { get; private init; }
    public string Reason { get; private init; }
    public DateTimeOffset OccurredAtUtc { get; private init; }
}
