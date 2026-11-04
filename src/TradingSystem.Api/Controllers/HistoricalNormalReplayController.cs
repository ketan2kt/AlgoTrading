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
    ITradingLotConfigurationService lots, IOptions<AutomatedPaperTradingOptions> automation,
    IOptions<MarketDataOptions> data, IOptions<PreliminaryRiskOptions> risk,
    IOptions<MarketRegimeOptions> regime, IOptions<PriceActionStrategyOptions> priceAction,
    IOptions<OpeningRangeBreakoutOptions> openingRange, TimeProvider clock,
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
        var sizes = await lots.GetAsync(cancellationToken);
        var value = automation.Value;
        var feed = data.Value;
        var admission = new NiftyOptionAdmissionSettings(value.PermissivePaperExecution,
            value.MaximumOptionSpreadPercent, value.MaximumOptionPremium, value.MinimumOptionVolume,
            value.MinimumOptionOpenInterest, value.OptionStopLossPercent, value.OptionRewardToRiskRatio,
            value.MaximumConcurrentPositions, risk.Value.MaximumRiskPerTrade, risk.Value.MaximumCapitalExposure,
            value.MaximumDailyLoss, TimeOnly.ParseExact(value.EntryWindowStart, "HH:mm"),
            TimeOnly.ParseExact(value.EntryCutoff, "HH:mm"), TimeOnly.ParseExact(value.ForcedExit, "HH:mm"),
            feed.MaximumAgeSeconds, feed.MaximumAgeSeconds, value.SelectiveHedgingEnabled,
            value.HedgeMinimumAtrPercent, value.HedgeMinimumRelativeFuturesVolume, value.HedgeMinimumSignalConfidence);
        var exits = new HistoricalNiftyExitSettings(admission.ForcedExitTime, value.BreakEvenEnabled,
            value.TrailingStopEnabled, value.PartialProfitEnabled, value.TargetExtensionEnabled,
            value.TargetExtensionMinimumConfidence, value.UnderlyingTrendInvalidationEnabled,
            value.MinimumReversalStructureStrength, value.RequiredReversalEvidenceCount,
            value.ProtectProfitableConfirmedReversals, value.ReversalProfitLockFraction,
            feed.MaximumAgeSeconds, feed.MaximumAgeSeconds, feed.CandleIntervalSeconds);
        var marketExits = new HistoricalMarketOptionExitSettings(new TimeOnly(15, 15),
            value.PartialProfitEnabled, value.TargetExtensionEnabled, value.UnderlyingTrendInvalidationEnabled,
            value.MinimumReversalStructureStrength, value.RequiredReversalEvidenceCount,
            value.ProtectProfitableConfirmedReversals, value.ReversalProfitLockFraction,
            feed.MaximumAgeSeconds, feed.MaximumAgeSeconds, feed.CandleIntervalSeconds);
        return Ok(new HistoricalNormalPolicyConfiguration(feed.CandleIntervalSeconds,
            value.MinimumSignalCandles, value.MinimumFuturesConfirmationCandles,
            TimeOnly.ParseExact(value.OpeningRangeEnd, "HH:mm"), sizes.NiftyLots, sizes.BankNiftyLots,
            value.OptionStopLossPercent, new(value.MaximumOptionDaysToExpiry,
                value.ExpiryDayInTheMoneySteps, value.NormalInTheMoneySteps, value.OptionStrikeStep),
            admission, exits, new(admission.EntryWindowStart, admission.EntryCutoff,
                new TimeOnly(15, 15), sizes.SensexLots, feed.CandleIntervalSeconds,
                value.OptionStopLossPercent, feed.MaximumAgeSeconds), marketExits,
            regime.Value, priceAction.Value, openingRange.Value)
            { WeeklyCarryForwardEnabled = value.WeeklyCarryForwardEnabled });
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
