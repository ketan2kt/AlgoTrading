using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TradingSystem.Application.Broker;

namespace TradingSystem.Api.Controllers;

[Authorize(Roles = "Administrator")]
[ApiController]
[Route("api/broker/groww/access-token")]
public sealed class GrowwTokenController(
    IGrowwTokenVault tokenVault,
    IGrowwInstrumentSynchronizer instrumentSynchronizer) : ControllerBase
{
    [HttpGet("status")]
    public async Task<ActionResult<GrowwTokenStatus>> GetStatus(
        CancellationToken cancellationToken) =>
        Ok(await tokenVault.GetStatusAsync(cancellationToken));

    [ValidateAntiForgeryToken]
    [HttpPost]
    public async Task<ActionResult<StoreGrowwTokenResponse>> Store(
        StoreGrowwTokenRequest request,
        CancellationToken cancellationToken)
    {
        if (request.AccessToken.Any(char.IsWhiteSpace))
        {
            ModelState.AddModelError(nameof(request.AccessToken),
                "The access token must not contain whitespace.");
            return ValidationProblem(ModelState);
        }

        var status = await tokenVault.StoreAsync(
            request.AccessToken,
            User.Identity?.Name ?? "administrator",
            cancellationToken);
        var synchronization = await instrumentSynchronizer.SynchronizeAsync(cancellationToken);

        return Ok(new StoreGrowwTokenResponse(status, synchronization));
    }

    [ValidateAntiForgeryToken]
    [HttpPost("synchronize-instruments")]
    public async Task<ActionResult<GrowwInstrumentSyncResult>> SynchronizeInstruments(
        CancellationToken cancellationToken) =>
        Ok(await instrumentSynchronizer.SynchronizeAsync(cancellationToken));
}

public sealed record StoreGrowwTokenResponse(
    GrowwTokenStatus Token,
    GrowwInstrumentSyncResult InstrumentSynchronization);

public sealed class StoreGrowwTokenRequest
{
    [Required, StringLength(8192, MinimumLength = 20)]
    public required string AccessToken { get; init; }
}
