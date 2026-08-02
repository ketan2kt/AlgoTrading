using Microsoft.AspNetCore.SignalR;
using TradingSystem.Application.MarketData;

namespace TradingSystem.Api.Hubs;

public sealed class SignalRLiveMarketDataPublisher(IHubContext<SystemHealthHub> hubContext)
    : ILiveMarketDataPublisher
{
    public Task PublishNiftyAsync(
        TradingWorkspaceSnapshot snapshot,
        CancellationToken cancellationToken) =>
        hubContext.Clients.Group(SystemHealthHub.NiftyWorkspaceGroup)
            .SendAsync("niftyWorkspaceUpdated", snapshot, cancellationToken);
}
