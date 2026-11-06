using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

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

public sealed class RecorderActivationHealthCheck(IOptions<HistoricalFeedCaptureOptions> options) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(options.Value.Enabled ? HealthCheckResult.Healthy() :
            HealthCheckResult.Unhealthy("Recorder capture is disabled."));
}
