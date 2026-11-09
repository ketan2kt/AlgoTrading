using TradingSystem.Application.Strategies;

namespace TradingSystem.Application.Execution;

public sealed record EntryChainConsistencyDecision(bool Permitted,
    ProfessionalTradeRegime PlanRegime, IReadOnlyList<string> Reasons);

// The Nifty caller has already passed timing, location, re-entry, OI and
// structure-quality gates. Do not let a second descriptive classifier veto
// that accepted setup. This does not override data, quote or portfolio risk.
public static class EntryChainConsistencyPolicy
{
    public const string Version = "validated-nifty-entry-chain-v1";

    public static EntryChainConsistencyDecision Evaluate(ProfessionalTradeRegime descriptiveRegime,
        PaperEntryQualityDecision quality)
    {
        ArgumentNullException.ThrowIfNull(quality);
        if (!quality.Permitted || descriptiveRegime == ProfessionalTradeRegime.Unavailable)
            return new(false, descriptiveRegime, quality.Permitted
                ? ["A valid market-state assessment is required."] : quality.Reasons);
        var planRegime = descriptiveRegime is ProfessionalTradeRegime.Chop or ProfessionalTradeRegime.Exhausted
            ? ProfessionalTradeRegime.Transition : descriptiveRegime;
        return new(true, planRegime, planRegime == descriptiveRegime
            ? ["Validated entry setup and admission plan agree."]
            : [$"Validated entry setup supersedes descriptive {descriptiveRegime}; use a Transition plan, not a high-conviction upgrade."]);
    }
}
