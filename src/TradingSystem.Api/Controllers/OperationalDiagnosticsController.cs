using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using TradingSystem.Application.Diagnostics;
using TradingSystem.Application.Execution;

namespace TradingSystem.Api.Controllers;

[ApiController]
[AllowAnonymous]
[EnableRateLimiting("diagnostics")]
[Route("api/internal/diagnostics")]
public sealed class OperationalDiagnosticsController(
    IOperationalAutomationReportReader operationalReports,
    IPaperTradingReportReader paperReports,
    IOptions<ReadOnlyDiagnosticsOptions> options) : ControllerBase
{
    private const string ApiKeyHeader = "X-Sarthi-Diagnostics-Key";

    [HttpGet("operational")]
    public async Task<ActionResult<IReadOnlyList<OperationalAutomationReport>>> GetOperational(
        [FromQuery] DateOnly? date,
        [FromQuery] string market = "all",
        CancellationToken cancellationToken = default)
    {
        var authorization = AuthorizeRequest();
        if (authorization is not null) return authorization;
        try
        {
            return Ok(await operationalReports.GetLatestAsync(date, market, cancellationToken));
        }
        catch (ArgumentOutOfRangeException exception)
        {
            ModelState.AddModelError(nameof(market), exception.Message);
            return ValidationProblem(ModelState);
        }
    }

    [HttpGet("replay")]
    public async Task<ActionResult<PaperStrategyReplayReport>> GetReplay(
        [FromQuery] int days = 30,
        CancellationToken cancellationToken = default)
    {
        var authorization = AuthorizeRequest();
        if (authorization is not null) return authorization;
        if (days is < 1 or > 90) return BadRequest("Days must be between 1 and 90.");
        return Ok((await paperReports.GetAsync(days, cancellationToken)).Replay);
    }

    private ActionResult? AuthorizeRequest()
    {
        Response.Headers.CacheControl = "no-store";
        var configuredKey = options.Value.ReadOnlyApiKey;
        if (!ReadOnlyDiagnosticsKeyValidator.IsConfigured(configuredKey)) return NotFound();
        var suppliedKey = Request.Headers[ApiKeyHeader].FirstOrDefault();
        return ReadOnlyDiagnosticsKeyValidator.IsAuthorized(configuredKey, suppliedKey)
            ? null
            : Unauthorized();
    }
}
