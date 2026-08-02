using TradingSystem.Domain.Common;

namespace TradingSystem.Domain.Trading;

public sealed class DailyRisk : MutableEntity
{
    public DailyRisk(
        Guid id,
        TradingMode mode,
        DateOnly tradingDate,
        DateTimeOffset createdAtUtc) : base(id, createdAtUtc)
    {
        Mode = mode;
        TradingDate = tradingDate;
    }

    public TradingMode Mode { get; private init; }
    public DateOnly TradingDate { get; private init; }
    public decimal RealisedPnl { get; private set; }
    public decimal UnrealisedPnl { get; private set; }
    public decimal RiskConsumed { get; private set; }
    public int TradeCount { get; private set; }
    public int ConsecutiveLosses { get; private set; }
    public bool IsEntryBlocked { get; private set; }
}

public sealed class TradingSession : MutableEntity
{
    public TradingSession(
        Guid id,
        TradingMode mode,
        DateOnly tradingDate,
        SessionState state,
        DateTimeOffset createdAtUtc) : base(id, createdAtUtc)
    {
        Mode = mode;
        TradingDate = tradingDate;
        State = state;
    }

    public TradingMode Mode { get; private init; }
    public DateOnly TradingDate { get; private init; }
    public SessionState State { get; private set; }
    public DateTimeOffset? StartedAtUtc { get; private set; }
    public DateTimeOffset? ReconciledAtUtc { get; private set; }
}

public sealed class ProviderHealth : MutableEntity
{
    public ProviderHealth(
        Guid id,
        string provider,
        DateTimeOffset createdAtUtc) : base(id, createdAtUtc)
    {
        Provider = provider;
    }

    public string Provider { get; private init; }
    public bool IsAvailable { get; private set; }
    public DateTimeOffset? LastSuccessAtUtc { get; private set; }
    public DateTimeOffset? LastFailureAtUtc { get; private set; }
    public string? ErrorCode { get; private set; }

    public void RecordSuccess(DateTimeOffset observedAtUtc)
    {
        IsAvailable = true;
        LastSuccessAtUtc = observedAtUtc.ToUniversalTime();
        ErrorCode = null;
    }

    public void RecordFailure(string errorCode, DateTimeOffset observedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(errorCode))
        {
            throw new ArgumentException("Error code is required.", nameof(errorCode));
        }

        IsAvailable = false;
        LastFailureAtUtc = observedAtUtc.ToUniversalTime();
        ErrorCode = errorCode;
    }
}

public sealed class SystemEvent : Entity, IAppendOnlyEntity
{
    public SystemEvent(
        Guid id,
        string severity,
        string eventType,
        string message,
        DateTimeOffset occurredAtUtc) : base(id)
    {
        Severity = severity;
        EventType = eventType;
        Message = message;
        OccurredAtUtc = occurredAtUtc.ToUniversalTime();
    }

    public string Severity { get; private init; }
    public string EventType { get; private init; }
    public string Message { get; private init; }
    public string? DetailsJson { get; private init; }
    public DateTimeOffset OccurredAtUtc { get; private init; }
}

public sealed class AuditLog : Entity, IAppendOnlyEntity
{
    public AuditLog(
        Guid id,
        string actor,
        string action,
        string entityType,
        string entityId,
        string reason,
        string beforeJson,
        string afterJson,
        string correlationId,
        DateTimeOffset occurredAtUtc) : base(id)
    {
        Actor = actor;
        Action = action;
        EntityType = entityType;
        EntityId = entityId;
        Reason = reason;
        BeforeJson = beforeJson;
        AfterJson = afterJson;
        CorrelationId = correlationId;
        OccurredAtUtc = occurredAtUtc.ToUniversalTime();
    }

    public string Actor { get; private init; }
    public string Action { get; private init; }
    public string EntityType { get; private init; }
    public string EntityId { get; private init; }
    public string Reason { get; private init; }
    public string BeforeJson { get; private init; }
    public string AfterJson { get; private init; }
    public string CorrelationId { get; private init; }
    public DateTimeOffset OccurredAtUtc { get; private init; }
}
