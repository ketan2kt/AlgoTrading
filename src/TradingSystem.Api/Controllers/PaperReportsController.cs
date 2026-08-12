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
}
