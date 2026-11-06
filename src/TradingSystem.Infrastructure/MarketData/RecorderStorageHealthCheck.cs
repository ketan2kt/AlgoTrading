using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace TradingSystem.Infrastructure.MarketData;

public sealed class RecorderStorageHealthCheck(ReplayBlobStorage storage) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await storage.VerifyAccessAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("Private replay storage verification failed: " + exception.GetType().Name);
        }
    }
}
