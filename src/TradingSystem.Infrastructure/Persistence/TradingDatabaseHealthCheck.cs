using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace TradingSystem.Infrastructure.Persistence;

public sealed class TradingDatabaseHealthCheck(TradingDbContext dbContext)
    : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!await dbContext.Database.CanConnectAsync(cancellationToken))
            {
                return HealthCheckResult.Unhealthy("PostgreSQL is not reachable.");
            }

            var pendingMigrations = await dbContext.Database
                .GetPendingMigrationsAsync(cancellationToken);
            var pending = pendingMigrations.ToArray();
            if (pending.Length > 0)
            {
                return HealthCheckResult.Unhealthy(
                    $"PostgreSQL has {pending.Length} pending migration(s).",
                    data: new Dictionary<string, object>
                    {
                        ["pendingMigrationCount"] = pending.Length
                    });
            }

            return HealthCheckResult.Healthy("PostgreSQL is reachable and schema is current.");
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy(
                "PostgreSQL connectivity check failed.",
                exception);
        }
    }
}
