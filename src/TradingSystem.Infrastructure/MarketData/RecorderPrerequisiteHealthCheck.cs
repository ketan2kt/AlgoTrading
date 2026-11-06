using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace TradingSystem.Infrastructure.MarketData;

// Separate from trading readiness: research storage must not gate trading/login.
public sealed class RecorderPrerequisiteHealthCheck(ReplayBlobStorage storage) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await storage.VerifyPublishedKeysAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("Recorder prerequisite unavailable: " + exception.GetType().Name);
        }
    }
}
