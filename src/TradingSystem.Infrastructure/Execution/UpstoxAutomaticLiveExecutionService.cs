using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingSystem.Infrastructure.Broker.Upstox;

namespace TradingSystem.Infrastructure.Execution;

// Independent bounded worker: an Upstox outage must not block management of Groww positions.
internal sealed class UpstoxAutomaticLiveExecutionService(IServiceScopeFactory scopes, UpstoxBrokerGateway broker,
    IOptions<LiveExecutionOptions> execution, IOptions<UpstoxExecutionOptions> settings,
    TimeProvider clock, ILogger<AutomaticLiveExecutionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!settings.Value.Enabled || !execution.Value.BuildEnabled) return;
        using var signal = new LiveExecutionWakeSignal();
        using var worker = new AutomaticLiveExecutionService(scopes, broker, signal, execution, clock, logger);
        await worker.StartAsync(stoppingToken);
        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { await worker.StopAsync(CancellationToken.None); }
    }
}
