using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using TradingSystem.Application.Execution;
using TradingSystem.Application.Regime;
using TradingSystem.Application.Risk;
using TradingSystem.Application.Strategies;
using TradingSystem.Infrastructure.Execution;
using TradingSystem.Infrastructure.MarketData;

namespace TradingSystem.Api.Controllers;

[Authorize(Roles = "Administrator")]
[ApiController]
[EnableRateLimiting("diagnostics")]
[Route("api/research/historical-replay/normal-policy")]
public sealed class HistoricalNormalReplayController(HistoricalNormalReplayComparisonService comparison,
    SessionPolicySettings settings, IRecordedPaperSessionReader recorded, TimeProvider clock,
    IHistoricalPolicyBuildStore builds, IHistoricalNormalReplayRunStore runs) : ControllerBase
{
    private static readonly SemaphoreSlim ComparisonSlot = new(1, 1);

    [HttpGet("runs")]
    public async Task<ActionResult<IReadOnlyList<HistoricalNormalReplayRunSummary>>> Runs(CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(await runs.ListAsync(cancellationToken));
    }

    [HttpGet("runs/{id}")]
    public async Task<ActionResult<HistoricalNormalReplayComparisonReport>> SavedReport(string id, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        try { return Ok((await runs.ViewAsync(id, cancellationToken)).Report with { SavedRunId = id }); }
        catch (ArgumentException) { return BadRequest("Invalid saved research run."); }
        catch (InvalidDataException) { return NotFound("Saved research run is absent or failed integrity validation."); }
    }

    [HttpGet("runs/{id}/configuration")]
    public async Task<ActionResult<HistoricalNormalReplayComparisonRequest>> SavedConfiguration(string id, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        try { return Ok((await runs.ViewAsync(id, cancellationToken)).Request); }
        catch (ArgumentException) { return BadRequest("Invalid saved research run."); }
        catch (InvalidDataException) { return NotFound("Saved research configuration is absent or failed integrity validation."); }
    }

    [HttpGet("builds")]
    public async Task<ActionResult<IReadOnlyList<HistoricalPolicyBuild>>> Builds(CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        await builds.CaptureCurrentAsync(cancellationToken);
        return Ok(await builds.ListAsync(cancellationToken));
    }

    [HttpGet("configuration")]
    public async Task<ActionResult<HistoricalNormalPolicyConfiguration>> Configuration(CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(await settings.ReadAsync(cancellationToken));
    }

    [HistoricalReplayWorkWindow]
    [ValidateAntiForgeryToken]
    [HttpPost("gap-aware/{date}")]
    public async Task<ActionResult<HistoricalGapAwareReport>> GapAware(DateOnly date,
        [FromServices] HistoricalGapAwareReplayService replay, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        if (date.Year < 2020 || !HistoricalReplaySessionCalendar.IsCompletedDate(date, clock.GetUtcNow()))
            return BadRequest("Choose a completed session.");
        if (!await ComparisonSlot.WaitAsync(0, cancellationToken))
            return Conflict("A normal-policy replay is already running. Retry when it finishes.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try { return Ok(await replay.RunAsync(date, timeout.Token)); }
        catch (InvalidDataException) { return Conflict("The saved replay input exceeded its bounds or failed integrity checks."); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return StatusCode(StatusCodes.Status408RequestTimeout, "Gap-aware replay exceeded its time budget; no incomplete verdict was returned."); }
        finally { ComparisonSlot.Release(); }
    }

    [HistoricalReplayWorkWindow]
    [ValidateAntiForgeryToken]
    [HttpPost("paper-session/{date}")]
    public async Task<ActionResult<HistoricalNormalReplayComparisonReport>> PaperSession(DateOnly date,CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl="no-store";
        if(date.Year<2020 || !HistoricalReplaySessionCalendar.IsCompletedDate(date,clock.GetUtcNow()))
            return BadRequest("Choose a completed session.");
        try
        {
            var receipt=await recorded.ReceiptAsync(date,cancellationToken);
            if(receipt is null) return Conflict("Inconclusive: verified pre-open policy is missing, conflicting, or lot settings changed. Current settings will not be substituted.");
            var execution=SessionPolicyReceipt.ComparisonExecution;
            return await Compare(new(date,date,receipt.Settings,receipt.Settings,execution) {
                BaselineBuildId=receipt.BuildId,ProposalBuildId=receipt.BuildId,PaperSessionPolicyFingerprint=receipt.Fingerprint
            },cancellationToken);
        }
        catch (InvalidDataException) { return Conflict("Inconclusive: dated policy evidence or strategy archive is invalid."); }
    }

    [ValidateAntiForgeryToken]
    [HttpPost("compare")]
    public async Task<ActionResult<HistoricalNormalReplayComparisonReport>> Compare(
        HistoricalNormalReplayComparisonRequest request, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        var local = clock.GetUtcNow().ToOffset(TimeSpan.FromHours(5.5));
        if (request.From == default || !HistoricalReplaySessionCalendar.IsCompletedDate(request.To, local) || request.From.Year < 2020 ||
            request.To < request.From || request.To.DayNumber - request.From.DayNumber > 30)
            return BadRequest("Choose completed dates in a range of up to 31 days.");
        var time = TimeOnly.FromDateTime(local.DateTime);
        if (local.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) &&
            time >= new TimeOnly(9, 0) && time <= new TimeOnly(15, 45))
            return Conflict("Historical comparisons run outside market hours to keep research work off the trading session.");
        if (!await ComparisonSlot.WaitAsync(0, cancellationToken))
            return Conflict("A normal-policy comparison is already running. Retry when it finishes.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var budget = TimeSpan.FromMinutes(2);
        if (local.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && time < new TimeOnly(9, 0))
        {
            var untilOpen = new DateTimeOffset(local.Date.AddHours(9), local.Offset) - local;
            if (untilOpen < budget) budget = untilOpen;
        }
        timeout.CancelAfter(budget);
        try
        {
            return Ok(await comparison.CompareAsync(request, timeout.Token));
        }
        catch (UnsupportedReplayContractException exception)
        {
            return Conflict(exception.Message);
        }
        catch (ArgumentException)
        {
            return BadRequest("The frozen research configuration or historical inputs are invalid.");
        }
        catch (InvalidDataException)
        {
            return Conflict("A saved strategy build or dataset is missing or failed integrity validation. No substitute was used.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return StatusCode(StatusCodes.Status408RequestTimeout, "Comparison exceeded its time budget. Try a shorter date range.");
        }
        finally { ComparisonSlot.Release(); }
    }
}
