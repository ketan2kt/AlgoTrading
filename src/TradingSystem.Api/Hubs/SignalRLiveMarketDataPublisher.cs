using Microsoft.AspNetCore.SignalR;
using TradingSystem.Application.MarketData;

namespace TradingSystem.Api.Hubs;

public sealed class SignalRLiveMarketDataPublisher(IHubContext<SystemHealthHub> hubContext)
    : ILiveMarketDataPublisher
{
    public Task PublishAsync(string market, TradingWorkspaceSnapshot snapshot,
        CancellationToken cancellationToken) =>
        hubContext.Clients.Group(SystemHealthHub.WorkspaceGroup(market))
            .SendAsync("marketWorkspaceUpdated", market, snapshot, cancellationToken);

    public Task PublishNiftyAsync(
        TradingWorkspaceSnapshot snapshot,
        CancellationToken cancellationToken) => PublishAsync("nifty", snapshot, cancellationToken);
}
