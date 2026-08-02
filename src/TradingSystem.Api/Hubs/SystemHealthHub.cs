using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace TradingSystem.Api.Hubs;

[Authorize]
public sealed class SystemHealthHub : Hub;

