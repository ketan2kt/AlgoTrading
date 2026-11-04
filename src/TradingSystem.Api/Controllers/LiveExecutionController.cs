using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using TradingSystem.Application.Risk;
using TradingSystem.Application.Diagnostics;

namespace TradingSystem.Api.Controllers;

[Authorize(Roles = "Administrator")]
[ApiController]
[Route("api/live-execution")]
public sealed class LiveExecutionController(
    ILiveTradingArmService armService) : ControllerBase
{
    [HttpGet("protection-monitor")]
    public async Task<ActionResult<ProtectionMonitorReport>> ProtectionMonitor([FromServices] ILiveProtectionMonitor reader,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl="no-store";
        return Ok(await reader.ReadAsync(cancellationToken));
    }

    [HttpGet("status")]
    public async Task<ActionResult<LiveTradingArmStatus>> Get(CancellationToken cancellationToken) =>
        Ok(await armService.GetAsync(cancellationToken));

    [ValidateAntiForgeryToken]
    [HttpPost("protection-monitor/{intentId:guid}/review")]
    public async Task<IActionResult> ReviewReminder(Guid intentId,ProtectionReminderReview request,
        [FromServices] ILiveProtectionMonitor monitor,CancellationToken cancellationToken)
    {
        if(request.Version is not {Length:64})return BadRequest();
        return await monitor.ReviewAsync(intentId,request.Version,User.Identity?.Name??"administrator",cancellationToken)
            ? NoContent() : Conflict("Reminder changed or is not eligible for review. Refresh to check the latest record.");
    }

    [EnableRateLimiting("authentication")]
    [ValidateAntiForgeryToken]
    [HttpPut("markets/{market}/arm")]
    public async Task<ActionResult<LiveTradingArmStatus>> Arm(string market, ArmLiveExecutionRequest request,
        CancellationToken cancellationToken)
    {
        if (!(await armService.GetAsync(cancellationToken)).AllowedMarkets.Contains(market,
            StringComparer.Ordinal)) return BadRequest("Unknown live market.");
        return Ok(await armService.SetAsync(market, request.Armed,
            User.Identity?.Name ?? "administrator", cancellationToken));
    }
}

public sealed class ArmLiveExecutionRequest
{
    public bool Armed { get; init; }
}

public sealed record ProtectionReminderReview(string Version);
