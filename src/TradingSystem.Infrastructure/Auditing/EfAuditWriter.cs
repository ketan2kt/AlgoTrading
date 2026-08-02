using TradingSystem.Application.Auditing;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.Infrastructure.Auditing;

public sealed class EfAuditWriter(TradingDbContext dbContext) : IAuditWriter
{
    public async Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        dbContext.AuditLogs.Add(new AuditLog(
            Guid.NewGuid(),
            entry.Actor,
            entry.Action,
            entry.EntityType,
            entry.EntityId,
            entry.Reason,
            entry.BeforeJson,
            entry.AfterJson,
            entry.CorrelationId,
            entry.OccurredAtUtc));

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}

