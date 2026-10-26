using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using TradingSystem.Application.Diagnostics;

namespace TradingSystem.Api.Controllers;

[ApiController]
[Authorize(Roles = "Administrator")]
[EnableRateLimiting("diagnostics")]
[Route("api/market-analysis/daily")]
public sealed class DailyAnalysisController(IDailyTradingDiagnosticsReader reader, TimeProvider clock) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<DailyTradingDiagnosticsReport>> Get([FromQuery] DateOnly? date,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        var now = clock.GetUtcNow();
        var today = DateOnly.FromDateTime(now.ToOffset(TimeSpan.FromHours(5.5)).DateTime);
        var requested = date ?? today;
        if (requested > today || requested < today.AddDays(-366)) return BadRequest("Invalid session date.");
        if (requested == today && now < new DateTimeOffset(today.ToDateTime(new TimeOnly(15, 45)), TimeSpan.FromHours(5.5)))
            return Conflict(new { message = "Today's analysis is available from 3:45 PM IST." });
        return Ok(await reader.ReadAsync(requested, "all", cancellationToken));
    }
}
