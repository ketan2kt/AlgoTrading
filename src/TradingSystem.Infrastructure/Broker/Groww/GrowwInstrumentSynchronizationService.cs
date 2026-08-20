using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TradingSystem.Application.Broker;

namespace TradingSystem.Infrastructure.Broker.Groww;

internal sealed partial class GrowwInstrumentSynchronizationService(
    IServiceScopeFactory scopeFactory,
    ILogger<GrowwInstrumentSynchronizationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var synchronizer = scope.ServiceProvider
                    .GetRequiredService<IGrowwInstrumentSynchronizer>();
                var result = await synchronizer.SynchronizeAsync(stoppingToken);
                LogSynchronizationCompleted(logger, result.Inserted, result.Updated, result.Skipped);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                LogSynchronizationFailed(logger, exception);
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }
    }

    [LoggerMessage(
        EventId = 1310,
        Level = LogLevel.Information,
        Message = "Groww trading instrument synchronization completed: {Inserted} inserted, {Updated} updated, {Skipped} skipped.")]
    private static partial void LogSynchronizationCompleted(
        ILogger logger,
        int inserted,
        int updated,
        int skipped);

    [LoggerMessage(
        EventId = 1311,
        Level = LogLevel.Error,
        Message = "Groww trading instrument synchronization failed; retrying in one minute.")]
    private static partial void LogSynchronizationFailed(ILogger logger, Exception exception);
}
