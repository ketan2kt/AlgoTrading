using TradingSystem.Domain.Common;

namespace TradingSystem.Domain.Trading;

public sealed class LiveExecutionIntent : MutableEntity
{
    public LiveExecutionIntent(Guid id, string sourceType, Guid sourceId, string market,
        Guid instrumentId, Direction direction, int quantity, decimal requestedEntry,
        decimal stopLoss, decimal target, string clientReference, DateTimeOffset createdAtUtc)
        : base(id, createdAtUtc)
    {
        if (string.IsNullOrWhiteSpace(sourceType) || sourceId == Guid.Empty ||
            string.IsNullOrWhiteSpace(market) || instrumentId == Guid.Empty || quantity <= 0 ||
            requestedEntry <= 0 || stopLoss <= 0 || target <= 0 ||
            string.IsNullOrWhiteSpace(clientReference))
            throw new ArgumentException("Live execution intent values are invalid.");
        SourceType = sourceType;
        SourceId = sourceId;
        Market = market;
        InstrumentId = instrumentId;
        Direction = direction;
        Quantity = quantity;
        RequestedEntry = requestedEntry;
        StopLoss = stopLoss;
        Target = target;
        ClientReference = clientReference;
    }

    public string SourceType { get; private init; }
    public Guid SourceId { get; private init; }
    public string Market { get; private init; }
    public Guid InstrumentId { get; private init; }
    public Direction Direction { get; private init; }
    public int Quantity { get; private init; }
    public decimal RequestedEntry { get; private init; }
    public decimal StopLoss { get; private set; }
    public decimal Target { get; private init; }
    public string ClientReference { get; private init; }
    public string Status { get; private set; } = "Pending";
    public string? BrokerOrderId { get; private set; }
    public string? ProtectionId { get; private set; }
    public int FilledQuantity { get; private set; }
    public int MirroredAddOnQuantity { get; private set; }
    public int MirroredPartialExitQuantity { get; private set; }
    public decimal? AverageFillPrice { get; private set; }
    public string? LastError { get; private set; }
    public DateTimeOffset? SubmittedAtUtc { get; private set; }
    public DateTimeOffset? ProtectedAtUtc { get; private set; }

    public void BeginEntrySubmission()
    {
        if (Status != "Pending")
            throw new InvalidOperationException("Only a pending live intent can begin broker submission.");
        Status = "EntrySubmitting";
    }

    public void Submitted(string brokerOrderId, DateTimeOffset atUtc)
    {
        BrokerOrderId = brokerOrderId;
        Status = "Submitted";
        SubmittedAtUtc = atUtc.ToUniversalTime();
        LastError = null;
    }

    public void Filled(int quantity, decimal averagePrice)
    {
        if (quantity <= 0 || averagePrice <= 0) throw new ArgumentException("Fill is invalid.");
        FilledQuantity = quantity;
        AverageFillPrice = averagePrice;
        Status = quantity < Quantity ? "PartiallyFilled" : "FilledUnprotected";
    }

    public void Protected(string protectionId, DateTimeOffset atUtc)
    {
        ProtectionId = protectionId;
        ProtectedAtUtc = atUtc.ToUniversalTime();
        Status = "Protected";
        LastError = null;
    }

    public void AdjustProtectedExposure(int remainingQuantity, decimal paperStopLoss,
        DateTimeOffset atUtc)
    {
        if (Status != "Protected" || remainingQuantity <= 0 || paperStopLoss <= 0)
            throw new InvalidOperationException("Only an open protected position can be adjusted.");
        FilledQuantity = remainingQuantity;
        StopLoss = paperStopLoss;
        MarkUpdated(atUtc.ToUniversalTime());
    }

    public void RecordAddOn(int quantity, DateTimeOffset atUtc)
    {
        if (Status != "Protected" || quantity <= 0)
            throw new InvalidOperationException("A live add-on requires an open protected position.");
        FilledQuantity = checked(FilledQuantity + quantity);
        MirroredAddOnQuantity = checked(MirroredAddOnQuantity + quantity);
        MarkUpdated(atUtc.ToUniversalTime());
    }

    public void RecordPartialExit(int quantity, DateTimeOffset atUtc)
    {
        if (Status != "Protected" || quantity <= 0 || quantity >= FilledQuantity)
            throw new InvalidOperationException("A partial exit must leave a protected position.");
        FilledQuantity -= quantity;
        MirroredPartialExitQuantity = checked(MirroredPartialExitQuantity + quantity);
        MarkUpdated(atUtc.ToUniversalTime());
    }

    public void BeginProtection()
    {
        if (FilledQuantity <= 0) throw new InvalidOperationException("An unfilled order cannot be protected.");
        Status = "ProtectionSubmitting";
    }

    public void RequireReconciliation(string reason)
    {
        Status = "ReconciliationRequired";
        LastError = reason;
    }

    public void FlagParityMismatch(string reason)
    {
        if (Status != "Protected" || string.IsNullOrWhiteSpace(reason))
            throw new InvalidOperationException("Only a protected live position can report a parity mismatch.");
        LastError = reason;
    }

    public void Reject(string reason, DateTimeOffset atUtc)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("A rejection reason is required.", nameof(reason));
        Status = "Rejected";
        LastError = reason;
        MarkUpdated(atUtc.ToUniversalTime());
    }

    public void Closed(DateTimeOffset atUtc)
    {
        Status = "Closed";
        FilledQuantity = 0;
        LastError = null;
        MarkUpdated(atUtc.ToUniversalTime());
    }
}
