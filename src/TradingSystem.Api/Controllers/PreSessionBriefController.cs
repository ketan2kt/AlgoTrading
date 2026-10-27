using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using TradingSystem.Application.MarketData;

namespace TradingSystem.Api.Controllers;

[ApiController]
[Authorize(Roles = "Administrator")]
[EnableRateLimiting("diagnostics")]
[Route("api/market-analysis/pre-session")]
public sealed class PreSessionBriefController(IPreSessionBriefReader reader, TimeProvider clock)
    : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PreSessionBriefReport>> Get([FromQuery] DateOnly? date,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        var today = DateOnly.FromDateTime(clock.GetUtcNow().ToOffset(TimeSpan.FromHours(5.5)).Date);
        var requested = date ?? today;
        if (requested > today || requested < today.AddDays(-366))
            return BadRequest("Invalid session date.");
        return Ok(await reader.ReadAsync(requested, cancellationToken));
    }
}
