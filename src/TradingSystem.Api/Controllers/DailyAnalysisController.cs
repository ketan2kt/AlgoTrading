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
        if (!DailyAnalysisAvailabilityPolicy.IsAvailable(requested, today, now))
            return Conflict(new { message = "Today's analysis is available from 3:15 PM IST." });
        return Ok(await reader.ReadAsync(requested, "all", cancellationToken));
    }

    [HttpGet("timeline")]
    public async Task<ActionResult<PaperSessionTimeline>> Timeline([FromQuery] DateOnly date,CancellationToken token)
    {
        Response.Headers.CacheControl="no-store";
        var local=clock.GetUtcNow().ToOffset(TimeSpan.FromHours(5.5)).DateTime;
        var today=DateOnly.FromDateTime(local);
        if(date>today || date<today.AddDays(-366))return BadRequest("Invalid session date.");
        if(date==today && TimeOnly.FromDateTime(local)<new TimeOnly(15,30))
            return Conflict(new {message="Session Timeline is available after 15:30 IST."});
        return Ok((await reader.ReadWithTimelineAsync(date,token)).Timeline);
    }
}
