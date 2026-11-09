namespace TradingSystem.Application.Execution;

public sealed class UnsupportedReplayContractException(string message) : InvalidOperationException(message);

// Final defence for a proposed compiled strategy that changes its selector.
// An out-of-universe candidate is a rejected replay, never a skipped losing trade.
public sealed class HistoricalContractCoveragePolicy(IHistoricalReplayPortfolioPolicy policy,
    string market, DateOnly date, IReadOnlySet<string> captured) : IHistoricalReplayPortfolioPolicy
{
    public string CodeVersion => policy.CodeVersion;
    public string ConfigurationManifest => policy.ConfigurationManifest;
    public bool ProductionPolicyParityVerified => policy.ProductionPolicyParityVerified;
    public IReadOnlyList<HistoricalReplayCandidate> Evaluate(HistoricalReplayPortfolioFrame frame)
    {
        var candidates = policy.Evaluate(frame);
        foreach (var candidate in candidates) Verify(candidate);
        return candidates;
    }
    public HistoricalReplayCandidate? AssessDeferredEntry(HistoricalReplayPortfolioFrame frame,
        HistoricalReplayCandidate pending, HistoricalReplayQuote optionQuote)
    {
        Verify(pending);
        var admitted = policy.AssessDeferredEntry(frame, pending, optionQuote);
        if (admitted is not null) Verify(admitted);
        return admitted;
    }
    private void Verify(HistoricalReplayCandidate candidate)
    {
        if (!captured.Contains(candidate.Symbol))
            throw new UnsupportedReplayContractException(FormattableString.Invariant(
                $"Unsupported replay: {market} on {date:yyyy-MM-dd} selected unrecorded contract {candidate.Symbol}. No comparison result was credited."));
    }
    public IReadOnlyList<HistoricalReplayStopUpdate> EvaluateStopUpdates(HistoricalReplayPortfolioFrame frame) => policy.EvaluateStopUpdates(frame);
    public IReadOnlyList<HistoricalReplayPartialExit> EvaluatePartialExits(HistoricalReplayPortfolioFrame frame) => policy.EvaluatePartialExits(frame);
    public IReadOnlyList<HistoricalReplayAddOn> EvaluateAddOns(HistoricalReplayPortfolioFrame frame) => policy.EvaluateAddOns(frame);
    public IReadOnlyList<HistoricalReplayExitRequest> EvaluateExits(HistoricalReplayPortfolioFrame frame) => policy.EvaluateExits(frame);
    public bool ShouldExtendTarget(HistoricalReplayPortfolioFrame frame, HistoricalReplayOpenPosition position) => policy.ShouldExtendTarget(frame, position);
}
