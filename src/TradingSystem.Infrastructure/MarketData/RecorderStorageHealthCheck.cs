using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using TradingSystem.Application.Broker;

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

public sealed class RecorderActivationHealthCheck(IOptions<HistoricalFeedCaptureOptions> options,
    ResearchCapturePulse pulse, TimeProvider clock, IHistoricalQuoteArchive archive) : IHealthCheck
{
    private static readonly string[] Markets = ["NIFTY", "SENSEX", "BANKNIFTY"];
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!options.Value.Enabled) return Task.FromResult(HealthCheckResult.Unhealthy("Recorder capture is disabled."));
        var now = clock.GetUtcNow();
        var india = now.ToOffset(TimeSpan.FromHours(5.5));
        var time = TimeOnly.FromDateTime(india.DateTime);
        if (india.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) &&
            time >= new TimeOnly(9, 0) && time < new TimeOnly(15, 35))
        {
            var feed = pulse.Read(DateOnly.FromDateTime(india.DateTime), true);
            if (feed.Phase != "Subscribed" || feed.Instruments <= 0)
                return Task.FromResult(HealthCheckResult.Unhealthy("Recorder feed has not subscribed."));
            if (time >= new TimeOnly(9, 15, 30) && Markets.Any(market =>
                    !feed.LastReceiptByMarket.TryGetValue(market, out var at) ||
                    at > now || now - at > TimeSpan.FromSeconds(30)))
                return Task.FromResult(HealthCheckResult.Unhealthy("Fresh feed receipts are required for all three markets."));
            if (time >= new TimeOnly(9, 15, 30))
            {
                var storage = archive.ReadHealth(DateOnly.FromDateTime(india.DateTime));
                if (storage?.Failure is not null || storage?.LastPersistedAtUtc is not { } written ||
                    written > now || now - written > TimeSpan.FromSeconds(30))
                    return Task.FromResult(HealthCheckResult.Unhealthy("A recent successful quote write is required."));
                if (Markets.Any(market => !storage.LastOptionWrittenAtByMarket.TryGetValue(market, out var optionAt) ||
                        optionAt > now || now - optionAt > TimeSpan.FromSeconds(30) ||
                        !storage.LastOptionSourceAtByMarket.TryGetValue(market, out var sourceAt) ||
                        sourceAt > now || now - sourceAt > TimeSpan.FromSeconds(30)))
                    return Task.FromResult(HealthCheckResult.Unhealthy("Fresh saved option prices AND depth are required for every market."));
            }
        }
        // Historical gaps do not prevent recovery, but remain in capture-health.
        return Task.FromResult(HealthCheckResult.Healthy());
    }
}
