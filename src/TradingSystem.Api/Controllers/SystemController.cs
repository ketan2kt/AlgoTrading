using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TradingSystem.Application.SystemStatus;
using TradingSystem.Application.MarketData;
using TradingSystem.Application.Regime;

namespace TradingSystem.Api.Controllers;

[ApiController]
[Route("api/system")]
public sealed class SystemController(ISystemStatusReader statusReader) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet("status")]
    public ActionResult<SystemStatusSnapshot> GetStatus() => Ok(statusReader.GetCurrent());

    [AllowAnonymous]
    [HttpGet("market-data-health")]
    public ActionResult<IReadOnlyList<MarketDataHealthSnapshot>> GetMarketDataHealth(
        [FromServices] IMarketDataHealthReader healthReader) => Ok(healthReader.GetCurrent());

    [AllowAnonymous]
    [HttpGet("futures-feed-health")]
    public ActionResult<FuturesFeedHealthSnapshot> GetFuturesFeedHealth(
        [FromServices] IFuturesFeedHealthReader healthReader) => Ok(healthReader.GetCurrent());

    [AllowAnonymous]
    [HttpGet("market-regime")]
    public ActionResult<MarketRegimeResult?> GetMarketRegime(
        [FromServices] IMarketRegimeReader regimeReader) => Ok(regimeReader.GetLatest());
}
