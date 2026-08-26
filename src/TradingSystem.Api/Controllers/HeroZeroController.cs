using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TradingSystem.Application.Execution;

namespace TradingSystem.Api.Controllers;

[Authorize(Roles = "Administrator")]
[ApiController]
[Route("api/hero-zero")]
public sealed class HeroZeroController(IHeroZeroMonitorReader reader) : ControllerBase
{
    [HttpGet("{market}")]
    public ActionResult<HeroZeroMonitorSnapshot> Get(string market)
    {
        if (market is not ("nifty" or "sensex")) return NotFound();
        return Ok(reader.GetSnapshot(market));
    }
}
