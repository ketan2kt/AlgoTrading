using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TradingSystem.Application.Risk;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.Infrastructure.Execution;

// Independent of order execution. Establishes dated evidence even for an untouched OFF switch.
internal sealed partial class LiveRoutingBaselineService(IServiceScopeFactory scopes, TimeProvider clock,
    ILogger<LiveRoutingBaselineService> logger) : BackgroundService
{
    internal const string Prefix = "LiveRouteBaseline:";
    private static readonly string[] Markets = ["NIFTY", "SENSEX", "BANK-NIFTY"];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var at = clock.GetUtcNow();
                var arm = await scope.ServiceProvider.GetRequiredService<ILiveTradingArmService>().GetAsync(stoppingToken);
                var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
                foreach (var market in Markets)
                {
                    var setting = arm.ForMarket(market);
                    var state = !arm.BuildEnabled ? "BuildDisabled" : setting is null ? "MarketExcluded" :
                        setting.Armed ? "On" : "Off";
                    var effectiveAt = setting?.ChangedAtUtc is { } changed && changed > at ? changed : at;
                    db.AuditLogs.Add(new AuditLog(Guid.NewGuid(), "system", Prefix + state, nameof(ApplicationSetting),
                        EfLiveTradingArmService.KeyPrefix + market, "Live routing state observed at startup",
                        "null", "null", Guid.NewGuid().ToString("N"), effectiveAt));
                }
                await db.SaveChangesAsync(stoppingToken);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { LogUnavailable(logger, ex.GetType().Name); }
            await Task.Delay(TimeSpan.FromMinutes(1), clock, stoppingToken);
        }
    }

    [LoggerMessage(LogLevel.Warning, "Live routing baseline evidence unavailable ({failureType}); retry scheduled.")]
    private static partial void LogUnavailable(ILogger logger, string failureType);
}
