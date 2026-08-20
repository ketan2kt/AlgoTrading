using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace TradingSystem.Api.Hubs;

[Authorize(Roles = "Administrator")]
public sealed class SystemHealthHub : Hub
{
    public const string NiftyWorkspaceGroup = "nifty-workspace";

    public static string WorkspaceGroup(string market) => $"market-workspace:{market}";

    public Task SubscribeNiftyWorkspace() =>
        Groups.AddToGroupAsync(Context.ConnectionId, NiftyWorkspaceGroup);

    public Task SubscribeMarketWorkspace(string market) =>
        Groups.AddToGroupAsync(Context.ConnectionId, WorkspaceGroup(market));
}
