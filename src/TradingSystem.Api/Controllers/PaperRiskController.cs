using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TradingSystem.Application.Risk;

namespace TradingSystem.Api.Controllers;

[Authorize(Roles = "Administrator")]
[ApiController]
[Route("api/paper/risk")]
public sealed class PaperRiskController(IPaperKillSwitchService killSwitch) : ControllerBase
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
}

public sealed record SetKillSwitchRequest(bool Active,
    [property: System.ComponentModel.DataAnnotations.Required,
     System.ComponentModel.DataAnnotations.StringLength(500, MinimumLength = 3)] string Reason);
