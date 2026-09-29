using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TradingSystem.Application.Execution;

namespace TradingSystem.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/market-analysis/research-pipeline")]
public sealed class DailyResearchPipelineController(IDailyResearchPipelineV2Reader reader)
    : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<DailyResearchPipelineReportV2>>> Get(
        CancellationToken cancellationToken) => Ok(await reader.GetLatestAsync(cancellationToken));
}
