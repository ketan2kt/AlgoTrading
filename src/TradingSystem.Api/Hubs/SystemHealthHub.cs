using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace TradingSystem.Api.Hubs;

[Authorize(Roles = "Administrator")]
public sealed class SystemHealthHub : Hub
{
    public const string NiftyWorkspaceGroup = "nifty-workspace";

    public Task SubscribeNiftyWorkspace() =>
        Groups.AddToGroupAsync(Context.ConnectionId, NiftyWorkspaceGroup);
}
