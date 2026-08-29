using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TradingSystem.Application.Execution;

namespace TradingSystem.Api.Controllers;

[Authorize(Roles = "Administrator")]
[ApiController]
[Route("api/reports/paper-trading")]
public sealed class PaperReportsController(IPaperTradingReportReader reader) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PaperTradingReport>> Get([FromQuery] int days = 30,
        CancellationToken cancellationToken = default) =>
        Ok(await reader.GetAsync(days, cancellationToken));

    [HttpGet("pnl-summary")]
    public async Task<ActionResult<PaperPnlSummary>> GetPnlSummary(
        [FromQuery] DateOnly from, [FromQuery] DateOnly to, [FromQuery] string market = "all",
        CancellationToken cancellationToken = default)
    {
        var normalized = market.Trim().ToLowerInvariant();
        if (from == default || to == default || from > to)
            return BadRequest("From and To dates are required and From cannot be after To.");
        if (to.DayNumber - from.DayNumber > 3660)
            return BadRequest("The selected range cannot exceed ten years.");
        if (normalized is not ("all" or "nifty" or "sensex" or "natural-gas"))
            return BadRequest("Market must be all, nifty, sensex, or natural-gas.");
        return Ok(await reader.GetPnlSummaryAsync(from, to, normalized, cancellationToken));
    }
}
