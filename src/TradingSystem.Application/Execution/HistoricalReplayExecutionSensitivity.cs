namespace TradingSystem.Application.Execution;

public sealed record HistoricalReplayExecutionScenario(decimal SlippageMultiplier,
    HistoricalPortfolioComparisonResult Comparison);

public sealed record HistoricalReplayExecutionSensitivityResult(
    IReadOnlyList<HistoricalReplayExecutionScenario> Scenarios)
{
    // A favorable modeled difference is not a verdict unless every underlying
    // comparison has independently verified policy and historical evidence.
    public bool HasCredibleVerdict => Scenarios.Count > 0 && Scenarios.All(value =>
        value.Comparison.EvidenceStatus == "ResearchOnly");
    public bool ValidationImprovesAcrossScenarios => HasCredibleVerdict &&
        Scenarios.All(value => value.Comparison.ValidationNetDifference > 0m);
}

// Offline stress pass over the same frozen sessions. It does not optimize a
// threshold or select a favorable fill assumption after seeing results.
public static class HistoricalReplayExecutionSensitivity
{
    private static readonly decimal[] Multipliers = [1m, 2m, 3m];

    public static HistoricalReplayExecutionSensitivityResult Run(
        IReadOnlyList<HistoricalReplaySessionData> sessions,
        Func<DateOnly, IHistoricalReplayPortfolioPolicy> baselineFactory,
        Func<DateOnly, IHistoricalReplayPortfolioPolicy> proposalFactory,
        HistoricalReplayParameters baselineParameters,
        HistoricalReplayParameters proposalParameters)
    {
        var scenarios = new List<HistoricalReplayExecutionScenario>(
            Multipliers.Length);
        foreach (var multiplier in Multipliers)
        {
            var baseline = Scale(baselineParameters, multiplier);
            var proposal = Scale(proposalParameters, multiplier);
            var comparison = HistoricalPortfolioComparison.Run(sessions,
                baselineFactory, proposalFactory, baseline, proposal);
            if (scenarios.Count > 0 && scenarios[0].Comparison.DataFingerprint !=
                comparison.DataFingerprint)
                throw new InvalidOperationException(
                    "Execution-sensitivity scenarios did not use identical market inputs.");
            if (scenarios.Count > 0 &&
                (scenarios[0].Comparison.BaselineCodeVersion !=
                    comparison.BaselineCodeVersion ||
                 scenarios[0].Comparison.ProposalCodeVersion !=
                    comparison.ProposalCodeVersion ||
                 scenarios[0].Comparison.PolicyConfigurationFingerprint !=
                    comparison.PolicyConfigurationFingerprint))
                throw new InvalidOperationException(
                    "Execution-sensitivity scenarios changed a strategy version or configuration.");
            scenarios.Add(new(multiplier, comparison));
        }
        return new(scenarios);
    }

    private static HistoricalReplayParameters Scale(
        HistoricalReplayParameters parameters, decimal multiplier)
    {
        var scaled = parameters with
        {
            EntrySlippagePercent = parameters.EntrySlippagePercent * multiplier,
            ExitSlippagePercent = parameters.ExitSlippagePercent * multiplier
        };
        scaled.Validate();
        return scaled;
    }
}
