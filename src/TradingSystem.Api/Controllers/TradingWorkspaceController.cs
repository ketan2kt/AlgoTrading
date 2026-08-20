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

    [HttpGet("{market}")]
    public async Task<ActionResult<TradingWorkspaceSnapshot>> GetMarket(
        string market, [FromQuery] int candleCount = 180,
        CancellationToken cancellationToken = default)
    {
        if (market is not ("sensex" or "natural-gas")) return NotFound();
        return Ok(await reader.GetAsync(market, candleCount, cancellationToken));
    }
}
