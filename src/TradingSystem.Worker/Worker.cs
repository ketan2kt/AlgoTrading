namespace TradingSystem.Worker;

public sealed partial class Worker(ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogWorkerStarted(logger);

        while (!stoppingToken.IsCancellationRequested)
        {
            LogHeartbeat(logger, DateTimeOffset.UtcNow);
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }

    [LoggerMessage(
        EventId = 1000,
        Level = LogLevel.Information,
        Message = "Foundation worker started. Trading execution is unavailable in Phase 1.")]
    private static partial void LogWorkerStarted(ILogger logger);

    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Debug,
        Message = "Foundation worker heartbeat at {ObservedAtUtc}")]
    private static partial void LogHeartbeat(ILogger logger, DateTimeOffset observedAtUtc);
}
