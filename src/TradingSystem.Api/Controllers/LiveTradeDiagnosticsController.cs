using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using TradingSystem.Application.Diagnostics;

namespace TradingSystem.Api.Controllers;

[ApiController]
[AllowAnonymous]
[EnableRateLimiting("diagnostics")]
[Route("api/internal/diagnostics/live-trade")]
public sealed class LiveTradeDiagnosticsController(
    ILiveTradeDiagnosticsReader reader,
    IOptions<ReadOnlyDiagnosticsOptions> options) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<LiveTradeDiagnosticsReport>> Get(
        [FromQuery] Guid tradeId, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        var configuredKey = options.Value.ReadOnlyApiKey;
        if (!ReadOnlyDiagnosticsKeyValidator.IsConfigured(configuredKey)) return NotFound();
        var suppliedKey = Request.Headers["X-Sarthi-Diagnostics-Key"].FirstOrDefault();
        if (!ReadOnlyDiagnosticsKeyValidator.IsAuthorized(configuredKey, suppliedKey))
            return Unauthorized();
        if (tradeId == Guid.Empty) return BadRequest("A tradeId is required.");
        var report = await reader.ReadAsync(tradeId, cancellationToken);
        return report is null ? NotFound() : Ok(report);
    }
}
