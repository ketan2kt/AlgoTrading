using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TradingSystem.Application.Execution;

namespace TradingSystem.Api.Controllers;

[Authorize(Roles = "Administrator")]
[ApiController]
[Route("api/market-analysis/self-improvement")]
public sealed class SelfImprovementResearchController(ISelfImprovementResearchReader reader)
    : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<SelfImprovementResearchReport>>> Get(
        CancellationToken cancellationToken = default) =>
        Ok(await reader.GetLatestAsync(cancellationToken));
}
