using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingSystem.Application.MarketData;

namespace TradingSystem.Infrastructure.MarketData;

// Dashboard refreshes may coalesce; market observations must never be dropped.
internal sealed class MarketWorkspaceRefreshQueue
{
    internal static readonly string[] Markets = ["nifty", "sensex", "bank-nifty"];
    private readonly Dictionary<string, Channel<bool>> channels = Markets.ToDictionary(
        market => market, _ => Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest }));

    public void Request(string market) => channels[market].Writer.TryWrite(true);
    public ChannelReader<bool> Read(string market) => channels[market].Reader;
}

internal sealed partial class MarketWorkspaceRefreshService(
    IServiceScopeFactory scopes, MarketWorkspaceRefreshQueue queue,
    IOptions<LiveNiftyOptions> options, ILogger<MarketWorkspaceRefreshService> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => !options.Value.Enabled
        ? Task.CompletedTask
        : Task.WhenAll(MarketWorkspaceRefreshQueue.Markets.Select(market => PublishAsync(market, stoppingToken)));

    private async Task PublishAsync(string market, CancellationToken stoppingToken)
    {
        await foreach (var _ in queue.Read(market).ReadAllAsync(stoppingToken))
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                await using var scope = scopes.CreateAsyncScope();
                var snapshot = await scope.ServiceProvider.GetRequiredService<ITradingWorkspaceReader>()
                    .GetAsync(market, options.Value.WorkspaceCandleCount, timeout.Token);
                await scope.ServiceProvider.GetRequiredService<ILiveMarketDataPublisher>()
                    .PublishAsync(market, snapshot, timeout.Token);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception) { LogRefreshFailed(logger, market, exception); }
            // Next queued notification retries; failures do not change quote-feed health.
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Market} dashboard refresh failed; quote polling continues independently.")]
    private static partial void LogRefreshFailed(ILogger logger, string market, Exception exception);
}
