using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TradingSystem.Application.Risk;

namespace TradingSystem.Api.Controllers;

[Authorize(Roles = "Administrator")]
[ApiController]
[Route("api/paper/risk")]
public sealed class PaperRiskController(
    IPaperKillSwitchService killSwitch,
    IPaperDailyLossOverrideService dailyLossOverride) : ControllerBase
{
    [HttpGet("kill-switch")]
    public async Task<ActionResult<PaperKillSwitchStatus>> Get(CancellationToken cancellationToken) =>
        Ok(await killSwitch.GetAsync(cancellationToken));

    [ValidateAntiForgeryToken]
    [HttpPut("kill-switch")]
    public async Task<ActionResult<PaperKillSwitchStatus>> Put(SetKillSwitchRequest request,
        CancellationToken cancellationToken)
        => Ok(await killSwitch.SetAsync(request.Active, request.Reason,
            User.Identity?.Name ?? "administrator", cancellationToken));

    [HttpGet("daily-loss-override")]
    public async Task<ActionResult<PaperDailyLossOverrideStatus>> GetDailyLossOverride(
        CancellationToken cancellationToken) =>
        Ok(await dailyLossOverride.GetAsync(cancellationToken));

    [ValidateAntiForgeryToken]
    [HttpPut("daily-loss-override")]
    public async Task<ActionResult<PaperDailyLossOverrideStatus>> PutDailyLossOverride(
        SetDailyLossOverrideRequest request, CancellationToken cancellationToken) =>
        Ok(await dailyLossOverride.SetAsync(request.Active, request.Reason,
            User.Identity?.Name ?? "administrator", cancellationToken));
}

public sealed class SetKillSwitchRequest
{
    public bool Active { get; init; }

    [System.ComponentModel.DataAnnotations.Required,
     System.ComponentModel.DataAnnotations.StringLength(500, MinimumLength = 3)]
    public required string Reason { get; init; }
}

public sealed class SetDailyLossOverrideRequest
{
    public bool Active { get; init; }

    [System.ComponentModel.DataAnnotations.Required,
     System.ComponentModel.DataAnnotations.StringLength(500, MinimumLength = 3)]
    public required string Reason { get; init; }
}
