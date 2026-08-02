using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TradingSystem.Application.Broker;
using TradingSystem.Domain;
using TradingSystem.Infrastructure.SystemStatus;

namespace TradingSystem.Infrastructure.Broker;

internal sealed partial class PaperBrokerRecoveryService(
    IServiceProvider serviceProvider,
    IOptions<TradingModeOptions> modeOptions,
    ILogger<PaperBrokerRecoveryService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (modeOptions.Value.Mode != TradingMode.Paper)
        {
            return;
        }

        var brokerGateway = serviceProvider.GetRequiredService<IBrokerGateway>();
        var positions = await brokerGateway.GetPositionsAsync(cancellationToken);
        LogRecoveryCompleted(logger, positions.Count);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(
        EventId = 3100,
        Level = LogLevel.Information,
        Message = "Paper broker journal recovery completed with {PositionCount} open positions.")]
    private static partial void LogRecoveryCompleted(ILogger logger, int positionCount);
}
