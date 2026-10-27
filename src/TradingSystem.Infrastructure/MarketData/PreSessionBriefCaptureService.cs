using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TradingSystem.Infrastructure.MarketData;

internal sealed class PreSessionBriefCaptureService(
    IServiceScopeFactory scopeFactory, TimeProvider clock,
    ILogger<PreSessionBriefCaptureService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await CaptureIfDueAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await CaptureIfDueAsync(stoppingToken);
    }

    private async Task CaptureIfDueAsync(CancellationToken cancellationToken)
    {
        var local = clock.GetUtcNow().ToOffset(TimeSpan.FromHours(5.5));
        if (local.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return;
        var time = TimeOnly.FromDateTime(local.DateTime);
        var date = DateOnly.FromDateTime(local.Date);
        foreach (var slot in EfPreSessionBriefReader.Stages)
        {
            // Never backfill an 8:45 or 9:12 brief with information acquired later.
            var captureMinutes = slot.Stage == "Outcome" ? 45 : 3;
            if (time < slot.At || time >= slot.At.AddMinutes(captureMinutes)) continue;
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<EfPreSessionBriefReader>()
                    .CaptureAsync(date, slot.Stage, clock.GetUtcNow(), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                logger.LogError(exception, "Pre-session {Stage} snapshot capture failed.", slot.Stage);
            }
        }
    }
}
