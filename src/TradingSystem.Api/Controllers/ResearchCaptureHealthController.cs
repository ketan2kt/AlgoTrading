using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TradingSystem.Infrastructure.MarketData;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.Api.Controllers;

// Safe during market hours: one dated master metadata lookup, no archive scan or Groww call.
[ApiController]
[Authorize(Roles = "Administrator")]
[EnableRateLimiting("diagnostics")]
[Route("api/research/capture-health")]
public sealed class ResearchCaptureHealthController(ResearchCapturePulse pulse, TradingDbContext db,
    IOptions<HistoricalFeedCaptureOptions> options, TimeProvider clock) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken token)
    {
        Response.Headers.CacheControl = "no-store";
        var now = clock.GetUtcNow().ToOffset(TimeSpan.FromHours(5.5));
        var date = DateOnly.FromDateTime(now.DateTime);
        var open = new DateTimeOffset(date.ToDateTime(new TimeOnly(9, 15)), now.Offset);
        var master = await db.HistoricalInstrumentMasterSnapshots.AsNoTracking()
            .Where(x => x.SessionDate == date && x.Source == "GrowwInstrumentMasterV1")
            .MinAsync(x => (DateTimeOffset?)x.CapturedAtUtc, token);
        return Ok(new { date, checkedAt = now, master, preOpenMaster = master <= open,
            feed = pulse.Read(date, options.Value.Enabled) });
    }
}
