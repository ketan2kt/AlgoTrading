using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TradingSystem.Application.Broker;
using TradingSystem.Application.Execution;

namespace TradingSystem.Infrastructure.Broker.Groww;

internal sealed partial class GrowwInstrumentSynchronizationService(
    IServiceScopeFactory scopeFactory,
    ILogger<GrowwInstrumentSynchronizationService> logger,
    TimeProvider clock) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await SynchronizeUntilSuccessAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = clock.GetUtcNow();
            var next = HistoricalInstrumentCaptureSchedule.NextPreOpenUtc(now);
            await Task.Delay(next - now, clock, stoppingToken);
            await CaptureUntilSuccessAsync(stoppingToken);
        }
    }

    private async Task SynchronizeUntilSuccessAsync(CancellationToken stoppingToken)
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

    private async Task CaptureUntilSuccessAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider
                    .GetRequiredService<IHistoricalInstrumentMasterCapturer>()
                    .CaptureAsync(stoppingToken);
                LogHistoricalCaptureCompleted(logger);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                LogHistoricalCaptureFailed(logger, exception);
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

    [LoggerMessage(EventId = 1312, Level = LogLevel.Information,
        Message = "Groww historical instrument master captured without updating trading instruments.")]
    private static partial void LogHistoricalCaptureCompleted(ILogger logger);

    [LoggerMessage(EventId = 1313, Level = LogLevel.Error,
        Message = "Groww historical instrument master capture failed; retrying in one minute.")]
    private static partial void LogHistoricalCaptureFailed(ILogger logger,
        Exception exception);
}
