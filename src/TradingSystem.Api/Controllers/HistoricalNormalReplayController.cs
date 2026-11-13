using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using TradingSystem.Application.Execution;
using TradingSystem.Infrastructure.Execution;

namespace TradingSystem.Api.Controllers;

[Authorize(Roles = "Administrator")]
[ApiController]
[EnableRateLimiting("diagnostics")]
[Route("api/research/historical-replay/normal-policy")]
public sealed class HistoricalNormalReplayController(SessionPolicySettings settings, TimeProvider clock,
    IHistoricalPolicyBuildStore builds, IHistoricalNormalReplayRunStore runs) : ControllerBase
{
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
            AcceptedAtAction(nameof(GapAwareStatus), new { id = job.Id }, PublicState(job));
    }

    [HttpGet("gap-aware/jobs/{id:guid}")]
    public async Task<ActionResult<GapReplayJob>> GapAwareStatus(Guid id,
        [FromServices] HistoricalGapReplayJobs jobs, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        var job = await jobs.ReadAsync(id, cancellationToken);
        return job is null ? NotFound("Saved replay job not found.") : Ok(PublicState(job));
    }

    [HistoricalReplayWorkWindow]
    [ValidateAntiForgeryToken]
    [HttpPost("paper-session/{date}")]
    public async Task<ActionResult<GapReplayJob>> PaperSession(DateOnly date,
        [FromServices] HistoricalGapReplayJobs jobs, CancellationToken cancellationToken, [FromQuery] Guid? requestId = null)
    {
        Response.Headers.CacheControl = "no-store";
        if (date.Year < 2020 || !HistoricalReplaySessionCalendar.IsCompletedDate(date, clock.GetUtcNow()))
            return BadRequest("Choose a completed session.");
        if (requestId == Guid.Empty) return BadRequest("Choose a valid replay request ID.");
        return await SubmitAsync(jobs, date, "paper-verification", requestId, cancellationToken);
    }

    [HistoricalReplayWorkWindow]
    [ValidateAntiForgeryToken]
    [HttpPost("compare")]
    public async Task<ActionResult<GapReplayJob>> Compare(HistoricalNormalReplayComparisonRequest request,
        [FromServices] HistoricalGapReplayJobs jobs, CancellationToken cancellationToken, [FromQuery] Guid? requestId = null)
    {
        Response.Headers.CacheControl = "no-store";
        if (request.From == default || !HistoricalReplaySessionCalendar.IsCompletedDate(request.To, clock.GetUtcNow()) ||
            request.From.Year < 2020 || request.To < request.From || request.To.DayNumber - request.From.DayNumber > 30)
            return BadRequest("Choose completed dates in a range of up to 31 days.");
        if (requestId == Guid.Empty) return BadRequest("Choose a valid replay request ID.");
        if (request.PaperSessionPolicyFingerprint is not null)
            return BadRequest("Use Verify against paper to select the recorded policy.");
        try
        {
            request.Execution.Validate();
            _ = new HistoricalNormalPolicyFactory(request.Baseline);
            _ = new HistoricalNormalPolicyFactory(request.Proposal);
        }
        catch (Exception exception) when (exception is ArgumentException or NullReferenceException)
        { return BadRequest("The frozen research configuration is invalid."); }
        // Bind blank selections to the approved code running at submission, not
        // a different release that might pick up this queued job tomorrow.
        var defaultBuildId = request.BaselineBuildId is null || request.ProposalBuildId is null
            ? (await builds.CaptureCurrentAsync(cancellationToken)).Id
            : request.BaselineBuildId;
        return await SubmitAsync(jobs, request.To, "strategy-comparison", requestId, cancellationToken, request, defaultBuildId);
    }

    private async Task<ActionResult<GapReplayJob>> SubmitAsync(HistoricalGapReplayJobs jobs, DateOnly date,
        string kind, Guid? requestId, CancellationToken token, HistoricalNormalReplayComparisonRequest? comparison = null,
        string? defaultBuildId = null)
    {
        var job = await jobs.EnqueueAsync(date, null, token, requestId, kind, comparison, defaultBuildId);
        return job is null ? Conflict("The replay queue is busy or this request ID belongs to another replay.") :
            AcceptedAtAction(nameof(GapAwareStatus), new { id = job.Id }, PublicState(job));
    }

    private static GapReplayJob PublicState(GapReplayJob job) =>
        job with { CompletedMarkets = null, ComparisonRequest = null, DefaultBuildId = null };
}
