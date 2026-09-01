using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using TradingSystem.Application.Risk;
using TradingSystem.Infrastructure.Identity;

namespace TradingSystem.Api.Controllers;

[Authorize(Roles = "Administrator")]
[ApiController]
[Route("api/live-execution")]
public sealed class LiveExecutionController(
    ILiveTradingArmService armService,
    UserManager<ApplicationUser> userManager) : ControllerBase
{
    [HttpGet("status")]
    public async Task<ActionResult<LiveTradingArmStatus>> Get(CancellationToken cancellationToken) =>
        Ok(await armService.GetAsync(cancellationToken));

    [EnableRateLimiting("authentication")]
    [ValidateAntiForgeryToken]
    [HttpPut("arm")]
    public async Task<ActionResult<LiveTradingArmStatus>> Arm(ArmLiveExecutionRequest request,
        CancellationToken cancellationToken)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null || !await userManager.CheckPasswordAsync(user, request.Password))
            return Unauthorized();
        return Ok(await armService.SetAsync(request.Armed, request.Reason,
            user.UserName ?? "administrator", cancellationToken));
    }
}

public sealed class ArmLiveExecutionRequest
{
    public bool Armed { get; init; }
    [Required, StringLength(500, MinimumLength = 10)] public required string Reason { get; init; }
    [Required, StringLength(256, MinimumLength = 1)] public required string Password { get; init; }
}
