namespace TradingSystem.Application.Execution;

// Retain the exact compiled module identity as well as a human policy version.
// A forgotten string-version bump must not reuse an older research run identity.
internal sealed class HistoricalCompiledPolicyVersion(IHistoricalReplayPortfolioPolicy policy)
    : IHistoricalReplayPortfolioPolicy
{
    public string CodeVersion => policy.CodeVersion + "|module:" +
        typeof(HistoricalCompiledPolicyVersion).Module.ModuleVersionId.ToString("D");
    public string ConfigurationManifest => policy.ConfigurationManifest;
    public bool ProductionPolicyParityVerified => policy.ProductionPolicyParityVerified;
    public IReadOnlyList<HistoricalReplayCandidate> Evaluate(HistoricalReplayPortfolioFrame frame) => policy.Evaluate(frame);
    public IReadOnlyList<HistoricalReplayStopUpdate> EvaluateStopUpdates(HistoricalReplayPortfolioFrame frame) => policy.EvaluateStopUpdates(frame);
    public IReadOnlyList<HistoricalReplayPartialExit> EvaluatePartialExits(HistoricalReplayPortfolioFrame frame) => policy.EvaluatePartialExits(frame);
    public IReadOnlyList<HistoricalReplayAddOn> EvaluateAddOns(HistoricalReplayPortfolioFrame frame) => policy.EvaluateAddOns(frame);
    public IReadOnlyList<HistoricalReplayExitRequest> EvaluateExits(HistoricalReplayPortfolioFrame frame) => policy.EvaluateExits(frame);
    public bool ShouldExtendTarget(HistoricalReplayPortfolioFrame frame, HistoricalReplayOpenPosition position) => policy.ShouldExtendTarget(frame, position);
    public HistoricalReplayCandidate? AssessDeferredEntry(HistoricalReplayPortfolioFrame frame,
        HistoricalReplayCandidate pending, HistoricalReplayQuote optionQuote) => policy.AssessDeferredEntry(frame, pending, optionQuote);
}
