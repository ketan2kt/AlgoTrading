using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using TradingSystem.Application.Broker;

namespace TradingSystem.Infrastructure.MarketData;

// Separate host from the Groww recorder. Uses the same contract selector and
// private archive, but never starts a Groww subscription or any order service.
internal sealed class UpstoxIndependentCaptureService(IConfiguration configuration,
    GrowwHistoricalFeedCaptureService planner, UpstoxReplayBackupCapture backup,
    IHistoricalQuoteArchive archive, ReplayBlobStorage cloud, TimeProvider clock) : BackgroundService
{
    internal static bool Enabled(IConfiguration configuration) =>
        configuration["ReplayBackup:Host"] == "Website" && !ReplayRecorderHosting.IsRecorder(configuration);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled(configuration)) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = clock.GetUtcNow().ToOffset(TimeSpan.FromHours(5.5));
            var date = DateOnly.FromDateTime(now.DateTime);
            var open = new DateTimeOffset(date.ToDateTime(new TimeOnly(9, 15)), now.Offset);
            if (now.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) &&
                now >= open.AddMinutes(-15) && now < open.AddMinutes(375))
            {
                try
                {
                    if (!backup.Enabled) throw new InvalidOperationException("Backup credential is not configured");
                    await cloud.RunBackupOwnerAsync(async ownedToken =>
                    {
                        var receipt = await cloud.ReadWebsitePolicyAsync(date, ownedToken);
                        var (plan, _) = await planner.LoadPlanAsync(date, receipt?.Settings.NiftySelection, ownedToken);
                        await backup.RunAsync(plan, new(archive), open, open.AddMinutes(375), ownedToken, persistAll: true);
                    }, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception error)
                {
                    foreach (var market in new[] { "NIFTY", "SENSEX", "BANKNIFTY" })
                        archive.SetBackupFailure(date, market, "Independent backup: " + error.GetType().Name);
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(15), clock, stoppingToken);
        }
    }
}
