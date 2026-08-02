using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TradingSystem.Application.MarketData;

namespace TradingSystem.Api.Controllers;

[Authorize(Roles = "Administrator")]
[ApiController]
[Route("api/trading-workspace")]
public sealed class TradingWorkspaceController(ITradingWorkspaceReader reader) : ControllerBase
{
    [HttpGet("nifty")]
    public async Task<ActionResult<TradingWorkspaceSnapshot>> GetNifty(
        [FromQuery] int candleCount = 180,
        CancellationToken cancellationToken = default) =>
        Ok(await reader.GetNiftyAsync(candleCount, cancellationToken));
}
