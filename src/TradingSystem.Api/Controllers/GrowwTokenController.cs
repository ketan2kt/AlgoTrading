using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TradingSystem.Application.Broker;

namespace TradingSystem.Api.Controllers;

[Authorize(Roles = "Administrator")]
[ApiController]
[Route("api/broker/groww/access-token")]
public sealed partial class GrowwTokenController(
    IGrowwTokenVault tokenVault,
    IGrowwInstrumentSynchronizer instrumentSynchronizer,
    ILogger<GrowwTokenController> logger) : ControllerBase
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
            User?.Identity?.Name ?? "administrator",
            cancellationToken);
        GrowwInstrumentSyncResult? synchronization = null;
        string? synchronizationError = null;
        try
        {
            synchronization = await instrumentSynchronizer.SynchronizeAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            LogInstrumentSynchronizationFailure(logger, exception);
            synchronizationError =
                "The token was stored, but Groww instruments could not be synchronized.";
        }

        return Ok(new StoreGrowwTokenResponse(status, synchronization, synchronizationError));
    }

    [ValidateAntiForgeryToken]
    [HttpPost("synchronize-instruments")]
    public async Task<ActionResult<GrowwInstrumentSyncResult>> SynchronizeInstruments(
        CancellationToken cancellationToken) =>
        Ok(await instrumentSynchronizer.SynchronizeAsync(cancellationToken));

    [LoggerMessage(
        EventId = 1301,
        Level = LogLevel.Error,
        Message = "Groww token was stored, but instrument synchronization failed.")]
    private static partial void LogInstrumentSynchronizationFailure(
        ILogger logger,
        Exception exception);
}

public sealed record StoreGrowwTokenResponse(
    GrowwTokenStatus Token,
    GrowwInstrumentSyncResult? InstrumentSynchronization,
    string? InstrumentSynchronizationError);

public sealed class StoreGrowwTokenRequest
{
    [Required, StringLength(8192, MinimumLength = 20)]
    public required string AccessToken { get; init; }
}
