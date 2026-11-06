using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TradingSystem.Infrastructure.MarketData;
using TradingSystem.Infrastructure.Persistence;
using TradingSystem.Application.Broker;

namespace TradingSystem.Api.Controllers;

// Safe during market hours: one dated master metadata lookup, no archive scan or Groww call.
[ApiController]
[Authorize(Roles = "Administrator")]
[EnableRateLimiting("diagnostics")]
[Route("api/research/capture-health")]
public sealed class ResearchCaptureHealthController(ResearchCapturePulse pulse, TradingDbContext db,
    IOptions<HistoricalFeedCaptureOptions> options, TimeProvider clock, IHistoricalQuoteArchive archive,
    ReplayBlobStorage cloud) : ControllerBase
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
        var feed = pulse.Read(date, options.Value.Enabled);
        var storage = archive.ReadHealth(date);
        ReplayRecorderSnapshot? remote = null;
        string? remoteError = null;
        // Staging cloud storage does not transfer capture ownership. Failed
        // cutover restores local capture; its real health must remain visible.
        var independentCapture = cloud.Enabled && !options.Value.Enabled;
        if (independentCapture)
        {
            try { remote = await cloud.ReadRecorderHealthAsync(token); }
            catch (Exception exception) when (exception is not OperationCanceledException) { remoteError = exception.GetType().Name; }
            if (remote?.Date == date) { feed = remote.Feed; storage = remote.Storage; }
        }
        var observedAt = remote?.Date == date ? remote.AtUtc : now;
        var expectedNow = now.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) &&
            now >= open.AddMinutes(-10) && now < open.AddHours(6.25);
        var quotesExpected = now >= open.AddSeconds(30);
        var issues = new List<string>();
        if (expectedNow)
        {
            if (independentCapture && (remote?.Date != date || now - remote.AtUtc > TimeSpan.FromSeconds(30) || remote.AtUtc > now))
                issues.Add("Independent recorder heartbeat missing/stale" + (remoteError is null ? "" : " · " + remoteError));
            if (cloud.LastFailure is { } cloudFailure) issues.Add("Private archive upload failed · " + cloudFailure);
            if (master is null || master > open) issues.Add("Pre-open instrument master missing");
            if (feed.Phase != "Subscribed") issues.Add("Feed " + feed.Phase + " · " + (feed.LastFailure ?? feed.Stage));
            else if (quotesExpected && feed.HasStalledMarket(observedAt)) issues.Add("Feed silent over 30 seconds");
            if (feed.GapObserved) issues.Add("Feed interruption recorded");
            if (!feed.PreOpenPolicyReady) issues.Add("Pre-open strategy/settings receipt missing");
            if (!feed.PriorSensexStateReady) issues.Add("Sensex prior-session state not yet recorded");
            if (storage?.Failure is { } failure) issues.Add(failure);
            if (storage?.GapObserved == true) issues.Add("Quote recording gap detected");
            if (storage is { ArchiveLimitBytes: > 0 } && storage.ArchiveBytes >= storage.ArchiveLimitBytes * .8m)
                issues.Add("Quote archive over 80% of storage limit");
            if (storage?.DiskFreeBytes is >= 0 and < 1024L * 1024 * 1024)
                issues.Add("Replay disk has less than 1 GB free");
            if (quotesExpected && (storage?.LastPersistedAtUtc is not { } written || observedAt - written > TimeSpan.FromSeconds(30)))
                issues.Add("No quote write confirmed in the last 30 seconds");
        }
        return Ok(new { date, checkedAt = now, master, preOpenMaster = master <= open,
            expectedNow, issues, feed, storage, recorderObservedAt = remote?.AtUtc, cloudUploadAt = remote?.LastUploadAtUtc });
    }
}
