namespace TradingSystem.Application.Execution;

public sealed record HistoricalNormalReplayComparisonRequest(DateOnly From, DateOnly To,
    HistoricalNormalPolicyConfiguration Baseline, HistoricalNormalPolicyConfiguration Proposal,
    HistoricalReplayParameters Execution)
{
    public string? BaselineBuildId { get; init; }
    public string? ProposalBuildId { get; init; }
    public string? FrozenInputRunId { get; init; }
}

public sealed record HistoricalNormalReplayComparisonReport(
    HistoricalSharedPortfolioResult Comparison, IReadOnlyList<string> Limitations)
{
    public string? BaselineBuildId { get; init; }
    public string? ProposalBuildId { get; init; }
    public string? SavedRunId { get; init; }
    public string? ExecutionBuildId { get; init; }
}

// Read-only archive + pure policies. Never resolve a broker, production engine,
// mutable trading control or the original accepted-trade candidate list.
public sealed class HistoricalNormalReplayComparisonService(IHistoricalReplayArchiveReader archive,
    IHistoricalPolicyBuildStore? builds = null, IHistoricalNormalReplayRunStore? runs = null,
    TimeProvider? clock = null)
{
    public async Task<HistoricalNormalReplayComparisonReport> CompareAsync(
        HistoricalNormalReplayComparisonRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.From == default || request.To < request.From ||
            request.To.DayNumber - request.From.DayNumber > 30)
            throw new ArgumentException("Choose an ordered completed range of up to 31 days.", nameof(request));
        request.Execution.Validate();
        var baseline = new HistoricalNormalPolicyFactory(request.Baseline);
        var proposal = new HistoricalNormalPolicyFactory(request.Proposal);
        var current = builds is null ? null : await builds.CaptureCurrentAsync(cancellationToken);
        using var baselineModule = builds is null ? null :
            await builds.LoadAsync(request.BaselineBuildId ?? current!.Id, cancellationToken);
        using var proposalModule = builds is null ? null :
            await builds.LoadAsync(request.ProposalBuildId ?? current!.Id, cancellationToken);
        if (builds is null && (request.BaselineBuildId is not null || request.ProposalBuildId is not null))
            throw new InvalidOperationException("Archived policy builds are unavailable; no current-code fallback was used.");
        var dates = Enumerable.Range(0, request.To.DayNumber - request.From.DayNumber + 1)
            .Select(request.From.AddDays).Where(date => date.DayOfWeek is not
                (DayOfWeek.Saturday or DayOfWeek.Sunday)).ToArray();
        var slices = new Dictionary<(DateOnly Date, string Market), HistoricalReplayArchiveSlice>();
        if (request.FrozenInputRunId is not null)
        {
            if (runs is null) throw new InvalidOperationException("Saved replay inputs are unavailable.");
            var saved = await runs.LoadAsync(request.FrozenInputRunId, cancellationToken);
            if (saved.Request.From != request.From || saved.Request.To != request.To)
                throw new ArgumentException("Saved input dates do not match the requested comparison.", nameof(request));
            foreach (var slice in saved.Inputs) slices.Add((slice.Session.Date, slice.Session.IndexSymbol), slice);
        }
        else
        {
        foreach (var date in dates)
        foreach (var market in new[] { "nifty", "sensex", "bank-nifty" })
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var slice = await archive.ReadAsync(date, market, cancellationToken);
                slices.Add((date, slice.Session.IndexSymbol), slice);
            }
            catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException)
            {
                // Missing/invalid input is an excluded date for BOTH versions,
                // not an excuse to backfill original accepted candidates.
            }
        }
        }
        var result = HistoricalSharedPortfolioComparison.RunEligible(
            slices.Values.Select(value => value.Session).ToArray(), ["NIFTY", "SENSEX", "BANKNIFTY"],
            (date, market) => baselineModule?.Create(request.Baseline, slices[(date, market)])
                ?? baseline.Create(slices[(date, market)]),
            (date, market) => proposalModule?.Create(request.Proposal, slices[(date, market)])
                ?? proposal.Create(slices[(date, market)]),
            request.Execution, request.Execution, dates, cancellationToken);
        var exclusions = result.Cohort.ExcludedDates.Select(value => new HistoricalReplayExcludedDate(value.Date,
            value.Reasons.Concat(slices.Values.Where(slice => slice.Session.Date == value.Date)
                .SelectMany(slice => slice.ControlStateGaps.Select(gap => slice.Session.IndexSymbol + ": " + gap)))
                .Distinct(StringComparer.Ordinal).ToArray())).ToArray();
        result = result with { Cohort = new(result.Cohort.IncludedSessions, exclusions) };
        var report = new HistoricalNormalReplayComparisonReport(result,
        [
            "Normal-session policy comparison only: Hero Zero and expansion pairs are deliberately excluded, as requested.",
            "Both versions use the same frozen data, execution model and date cohort; insufficient old dates are excluded.",
            "Approved archived compiled normal-policy builds can be compared using explicit frozen configurations. Arbitrary uploaded code is not accepted.",
            "Intraday, flat-start research only. Enabled overnight carry is rejected. Unmatched original manual interventions and reconciliation-outage dates are excluded, not silently applied to different simulated trades.",
            "Verified decision scope: normal 1/5-minute entries and protection, with as-of pre-session state. Fill latency, spreads, slippage and charges remain explicit models. Non-whole-lot paper partials invalidate execution coverage instead of silently being credited or rounded.",
            "Current or explicitly saved settings are used; they are not presented as recovered historical controls. Executable quote/universe coverage and supported-policy validation still govern the verdict."
        ])
        {
            BaselineBuildId = baselineModule?.BuildId,
            ProposalBuildId = proposalModule?.BuildId,
            ExecutionBuildId = current?.Id
        };
        if (runs is not null && current is not null)
        {
            var frozenRequest = request with { BaselineBuildId = report.BaselineBuildId,
                ProposalBuildId = report.ProposalBuildId };
            var id = await runs.SaveAsync(new((clock ?? TimeProvider.System).GetUtcNow(), current.Id,
                frozenRequest, slices.Values.OrderBy(value => value.Session.Date)
                    .ThenBy(value => value.Session.IndexSymbol, StringComparer.Ordinal).ToArray(), report), cancellationToken);
            report = report with { SavedRunId = id };
        }
        return report;
    }
}
