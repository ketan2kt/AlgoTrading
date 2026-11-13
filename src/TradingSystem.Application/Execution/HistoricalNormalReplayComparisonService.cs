namespace TradingSystem.Application.Execution;

public sealed record HistoricalNormalReplayComparisonRequest(DateOnly From, DateOnly To,
    HistoricalNormalPolicyConfiguration Baseline, HistoricalNormalPolicyConfiguration Proposal,
    HistoricalReplayParameters Execution)
{
    public string? BaselineBuildId { get; init; }
    public string? ProposalBuildId { get; init; }
    public string? FrozenInputRunId { get; init; }
    public string? PaperSessionPolicyFingerprint { get; init; }
}

public sealed record HistoricalNormalReplayComparisonReport(
    HistoricalSharedPortfolioResult Comparison, IReadOnlyList<string> Limitations)
{
    public string? BaselineBuildId { get; init; }
    public string? ProposalBuildId { get; init; }
    public string? SavedRunId { get; init; }
    public string? ExecutionBuildId { get; init; }
    public string? ExperimentFinding { get; init; }
    public bool VerifiedControlWithFills { get; init; }
    public PaperSessionFidelity? PaperFidelity { get; init; }
}

// Read-only archive + pure policies. Never resolve a broker, production engine,
// mutable trading control or the original accepted-trade candidate list.
public sealed class HistoricalNormalReplayComparisonService(IHistoricalReplayArchiveReader archive,
    IHistoricalPolicyBuildStore? builds = null, IHistoricalNormalReplayRunStore? runs = null,
    TimeProvider? clock = null, IRecordedPaperSessionReader? recorded = null)
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
                var slice = await archive.ReadStrictAsync(date, market, cancellationToken);
                slices.Add((date, slice.Session.IndexSymbol), slice);
            }
            catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException)
            {
                // Missing/invalid input is an excluded date for BOTH versions,
                // not an excuse to backfill original accepted candidates.
            }
        }
        }
        foreach (var key in slices.Keys.ToArray())
        {
            var slice = slices[key];
            var coverage = new List<string>(slice.RecordedContractCoverageIssues);
            if (baselineModule is not null && baselineModule.ContractSelectionVersion != RegularOptionContractSelector.Version)
                coverage.Add("Baseline compiled strategy does not declare a supported contract-selection contract.");
            if (proposalModule is not null && proposalModule.ContractSelectionVersion != RegularOptionContractSelector.Version)
                coverage.Add("Proposal compiled strategy does not declare a supported contract-selection contract.");
            coverage.AddRange(ReplayContractCoverage.ValidateReplay(slice, request.Baseline).Select(r => "Baseline: " + r));
            coverage.AddRange(ReplayContractCoverage.ValidateReplay(slice, request.Proposal).Select(r => "Proposal: " + r));
            slices[key] = slice with { Session = slice.Session with { UnsupportedContractCoverage = coverage } };
        }
        var execution = request.Execution with { RequireDisplayedFillSize = true };
        var result = HistoricalSharedPortfolioComparison.RunEligible(
            slices.Values.Select(value => value.Session).ToArray(), ["NIFTY", "SENSEX", "BANKNIFTY"],
            (date, market) => Guard(baselineModule?.Create(request.Baseline, slices[(date, market)])
                ?? baseline.Create(slices[(date, market)]), date, market),
            (date, market) => Guard(proposalModule?.Create(request.Proposal, slices[(date, market)])
                ?? proposal.Create(slices[(date, market)]), date, market),
            execution, execution, dates, cancellationToken);
        var exclusions = result.Cohort.ExcludedDates.Select(value => new HistoricalReplayExcludedDate(value.Date,
            value.Reasons.Concat(slices.Values.Where(slice => slice.Session.Date == value.Date)
                .SelectMany(slice => slice.ControlStateGaps.Select(gap => slice.Session.IndexSymbol + ": " + gap)))
                .Distinct(StringComparer.Ordinal).ToArray())).ToArray();
        result = result with { Cohort = new(result.Cohort.IncludedSessions, exclusions) };
        var experiment = HistoricalExperimentReview.Assess(request, result, baselineModule?.BuildId == proposalModule?.BuildId);
        PaperSessionFidelity? fidelity=null;
        if(request.PaperSessionPolicyFingerprint is { } expected){
            if(request.From!=request.To || recorded is null || request.FrozenInputRunId is not null ||
                request.Execution!=SessionPolicyReceipt.ComparisonExecution)
                throw new ArgumentException("Use a single newly captured session for paper fidelity.");
            var receipt=await recorded.ReceiptAsync(request.From,cancellationToken);
            if(receipt is null || receipt.Fingerprint!=expected || receipt.BuildId!=baselineModule?.BuildId ||
                receipt.BuildId!=proposalModule?.BuildId || slices.Count!=3 ||
                slices.Values.Any(x=>x.SessionPolicyFingerprint!=expected) ||
                SessionPolicyReceipt.Create(receipt.Date,receipt.ObservedAt,receipt.BuildId,request.Baseline).Fingerprint!=expected ||
                SessionPolicyReceipt.Create(receipt.Date,receipt.ObservedAt,receipt.BuildId,request.Proposal).Fingerprint!=expected)
                fidelity=new("Inconclusive",0,0,["Captured build, settings or common three-market archive not verified."],[]);
            else if(result.Days.Count!=1)
            {
                var actual = await recorded.ReadTradesAsync(request.From, cancellationToken);
                fidelity = new("Not simulated: strict coverage incomplete", actual.Trades.Count, 0,
                    result.Cohort.ExcludedDates.SelectMany(d=>d.Reasons)
                        .DefaultIfEmpty("Session not eligible.").Concat(actual.Gaps).Distinct().ToArray(), []);
            }
            else {
                // Read paper outcomes only after both simulations finish. Never seed entries with them.
                var actual=await recorded.ReadTradesAsync(request.From,cancellationToken);
                var replay=result.Days[0].Baseline;
                if(replay.MissingEntryQuoteCount+replay.MissingExitQuoteCount+replay.MissingAddOnQuoteCount+
                    replay.StaleQuoteCount+replay.UnsupportedPartialCount>0 || !experiment.VerifiedControlWithFills && replay.Trades.Count>0)
                    actual=actual with {Gaps=actual.Gaps.Append("Replay execution evidence or identical-build control is incomplete.").ToArray()};
                fidelity=PaperSessionFidelity.Compare(actual,replay.Trades);
            }
        }
        var report = new HistoricalNormalReplayComparisonReport(result,
        [
            "Normal-session policy comparison only: Hero Zero and expansion pairs are deliberately excluded, as requested.",
            "Both versions use the same frozen data, execution model and date cohort; insufficient old dates are excluded.",
            "Contract selection is shared by trading, recording and replay. Both versions must prove their entire session price-range selections exist in the saved universe; unsupported strikes, expiries, selection builds or missing manifests explicitly exclude the date before simulation. Pre-open coverage is a bounded plan, not unlimited future-strategy coverage.",
            "Approved archived compiled normal-policy builds can be compared using explicit frozen configurations. Arbitrary uploaded code is not accepted.",
            "Intraday, flat-start research only. Enabled overnight carry is rejected. Unmatched original manual interventions and reconciliation-outage dates are excluded, not silently applied to different simulated trades.",
            "Verified decision scope: normal 1/5-minute entries and protection, with as-of pre-session state. Orders wait for fresh observed bid/offer size that covers the whole order. Fill latency, slippage and charges remain modeled; unresolved executions and unsupported paper partials are not proof of strategy performance.",
            "Current or explicitly saved settings are used; they are not presented as recovered historical controls. Executable quote/universe coverage and supported-policy validation still govern the verdict."
        ])
        {
            BaselineBuildId = baselineModule?.BuildId,
            ProposalBuildId = proposalModule?.BuildId,
            ExecutionBuildId = current?.Id,
            ExperimentFinding = experiment.Finding,
            PaperFidelity = fidelity,
            VerifiedControlWithFills = experiment.VerifiedControlWithFills
        };
        if (runs is not null && current is not null &&
            (request.PaperSessionPolicyFingerprint is null || result.Days.Count != 0))
        {
            var frozenRequest = request with { BaselineBuildId = report.BaselineBuildId,
                ProposalBuildId = report.ProposalBuildId };
            var id = await runs.SaveAsync(new((clock ?? TimeProvider.System).GetUtcNow(), current.Id,
                frozenRequest, slices.Values.OrderBy(value => value.Session.Date)
                    .ThenBy(value => value.Session.IndexSymbol, StringComparer.Ordinal).ToArray(), report), cancellationToken);
            report = report with { SavedRunId = id };
        }
        return report;

        HistoricalContractCoveragePolicy Guard(IHistoricalReplayPortfolioPolicy policy, DateOnly date, string market) =>
            new(policy, market, date, slices[(date, market)].RecordedContractUniverse!.Instruments
                .Where(c => c.Market == market).Select(c => c.TradingSymbol).ToHashSet(StringComparer.Ordinal));
    }
}
