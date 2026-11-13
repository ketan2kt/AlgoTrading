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
    private static SemaphoreSlim ComparisonSlot => HistoricalGapReplayJobs.ComputeSlot;
    private static readonly TimeSpan ComparisonBudget = TimeSpan.FromMinutes(2);

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
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try { return Ok(await builds.ListAsync(timeout.Token)); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return StatusCode(StatusCodes.Status408RequestTimeout, "The archived build catalogue exceeded its time budget."); }
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
    public async Task<ActionResult<GapReplayJob>> GapAware(DateOnly date,
        [FromServices] HistoricalGapReplayJobs jobs, CancellationToken cancellationToken,
        [FromQuery] string? market = null, [FromQuery] Guid? requestId = null)
    {
        Response.Headers.CacheControl = "no-store";
        if (date.Year < 2020 || !HistoricalReplaySessionCalendar.IsCompletedDate(date, clock.GetUtcNow()))
            return BadRequest("Choose a completed session.");
        if (market is not (null or "nifty" or "sensex" or "bank-nifty"))
            return BadRequest("Choose Nifty, Sensex or Bank Nifty.");
        if (requestId == Guid.Empty) return BadRequest("Choose a valid replay request ID.");
        var job = await jobs.EnqueueAsync(date, market, cancellationToken, requestId);
        return job is null ? Conflict("The replay queue is busy. Retry shortly.") :
            AcceptedAtAction(nameof(GapAwareStatus), new { id = job.Id }, job with { CompletedMarkets = null });
    }

    [HttpGet("gap-aware/jobs/{id:guid}")]
    public async Task<ActionResult<GapReplayJob>> GapAwareStatus(Guid id,
        [FromServices] HistoricalGapReplayJobs jobs, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        var job = await jobs.ReadAsync(id, cancellationToken);
        return job is null ? NotFound("Saved replay job not found.") : Ok(job with { CompletedMarkets = null });
    }

    [HistoricalReplayWorkWindow]
    [ValidateAntiForgeryToken]
    [HttpPost("paper-session/{date}")]
    public async Task<ActionResult<HistoricalNormalReplayComparisonReport>> PaperSession(DateOnly date,CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl="no-store";
        if(date.Year<2020 || !HistoricalReplaySessionCalendar.IsCompletedDate(date,clock.GetUtcNow()))
            return BadRequest("Choose a completed session.");
        if (!await ComparisonSlot.WaitAsync(0, cancellationToken))
            return Conflict("A normal-policy replay is already running. Retry when it finishes.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(AvailableBudget());
        var stage = "capture and policy preflight";
        try
        {
            var preflight = await recorded.PreflightAsync(date, timeout.Token);
            if (preflight.CaptureExclusion is { } reason)
            {
                var cohort = new HistoricalReplayCohortSelection(Array.Empty<HistoricalReplaySessionData>(), [new(date, [reason])]);
                var excluded = new HistoricalSharedPortfolioResult(cohort, [], "", "", 0, 0, "InsufficientCoverage", [reason]);
                return Ok(new HistoricalNormalReplayComparisonReport(excluded,
                    ["Use gap-aware replay to inspect individual markets without treating unverified fills as matches."])
                {
                    PaperFidelity = new("Not simulated: strict coverage incomplete", preflight.PaperTrades ?? 0, 0, [reason], [])
                });
            }
            var receipt = preflight.Receipt;
            if(receipt is null) return Conflict("Inconclusive: verified pre-open policy is missing, conflicting, or lot settings changed. Current settings will not be substituted.");
            var execution=SessionPolicyReceipt.ComparisonExecution;
            stage = "input assembly or simulation";
            return Ok(await comparison.CompareAsync(new(date,date,receipt.Settings,receipt.Settings,execution) {
                BaselineBuildId=receipt.BuildId,ProposalBuildId=receipt.BuildId,PaperSessionPolicyFingerprint=receipt.Fingerprint
            }, timeout.Token));
        }
        catch (UnsupportedReplayContractException exception) { return Conflict(exception.Message); }
        catch (ArgumentException) { return BadRequest("The frozen research configuration or historical inputs are invalid."); }
        catch (InvalidDataException) { return Conflict("Inconclusive: dated policy evidence or strategy archive is invalid."); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return StatusCode(StatusCodes.Status408RequestTimeout, $"Paper verification exceeded its time budget during {stage}."); }
        finally { ComparisonSlot.Release(); }
    }

    private TimeSpan AvailableBudget()
    {
        var local = clock.GetUtcNow().ToOffset(TimeSpan.FromHours(5.5));
        if (local.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && local.TimeOfDay < TimeSpan.FromHours(9))
            return TimeSpan.FromTicks(Math.Min(ComparisonBudget.Ticks, (TimeSpan.FromHours(9) - local.TimeOfDay).Ticks));
        return ComparisonBudget;
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
