namespace TradingSystem.Application.Auditing;

public interface IAuditWriter
{
    Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken);
}

public sealed record AuditEntry(
    string Actor,
    string Action,
    string EntityType,
    string EntityId,
    string Reason,
    string BeforeJson,
    string AfterJson,
    string CorrelationId,
    DateTimeOffset OccurredAtUtc);

