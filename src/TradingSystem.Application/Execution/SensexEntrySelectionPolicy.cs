using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Execution;

// Shared, side-effect-free ordering of the existing Sensex candidate paths.
// Keeping this priority in one place lets a historical clock reconstruct the
// same candidate before the later entry-quality and option-risk gates.
public sealed record SensexEntryCandidate(Direction? Direction,
    decimal Confidence, string Strategy, IReadOnlyList<string> Reasons);

public sealed record SensexEntrySelection(SensexEntryCandidate Candidate,
    bool Supplemental, bool PersistentDirectional,
    SensexEarlyPullbackDecision? EarlyCandidate,
    SensexPaperResearchEntryDecision? EarlyAssessment);

public static class SensexEntrySelectionPolicy
{
    public const string Version = "sensex-entry-selection-v3";
    public const decimal MaximumPersistentEntryExtensionAtr = 1.50m;
    public static SensexEntrySelection Evaluate(
        SensexEntryCandidate primary,
        IReadOnlyList<StrategyPriceBar> bars,
        SensexSetupLifecycle lifecycle,
        PersistentDirectionalThesisV1? thesis,
        MarketStateAssessmentV2 marketState,
        decimal vwap, decimal fastEma, decimal slowEma,
        bool hasActivePosition)
    {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(marketState);
        var candidate = primary;
        var supplemental = false;
        var persistent = false;
        SensexEarlyPullbackDecision? earlyCandidate = null;
        SensexPaperResearchEntryDecision? earlyAssessment = null;
        if (thesis is not null)
        {
            var thesisEntry = PersistentDirectionalPaperEntryPolicyV1.Evaluate(
                bars, thesis);
            if (!hasActivePosition && thesisEntry.Direction is not null &&
                (candidate.Direction is null ||
                 candidate.Direction == thesisEntry.Direction))
            {
                candidate = new(thesisEntry.Direction.Value,
                    thesisEntry.Confidence, "Sensex persistent directional thesis",
                    thesisEntry.Reasons);
                supplemental = true;
                persistent = true;
            }
        }
        if (candidate.Direction is null)
        {
            var early = SensexEarlyPullbackResearchPolicy.Evaluate(bars);
            if (early.Direction is null)
                early = SensexPaperResearchEntryPolicy.DeriveLifecycleCandidate(
                    lifecycle);
            earlyCandidate = early;
            var assessment = SensexPaperResearchEntryPolicy.Evaluate(
                early.Direction, early.Confidence, lifecycle,
                hasActivePosition, thesis);
            earlyAssessment = assessment;
            if (assessment.Permitted)
            {
                candidate = new(early.Direction!.Value,
                    assessment.EffectiveConfidence,
                    "Sensex controlled early pullback",
                    [.. early.Reasons, .. assessment.Reasons]);
                supplemental = true;
            }
            if (!supplemental && !hasActivePosition)
            {
                var range = RangePlaybookV2.Evaluate(bars, marketState);
                if (range.Direction is not null)
                {
                    candidate = new(range.Direction.Value, range.Confidence,
                        "Sensex range-edge rejection", range.Reasons);
                    supplemental = true;
                }
            }
            if (!supplemental && !hasActivePosition)
            {
                var directional = DirectionalParticipationPolicy.Evaluate(
                    bars, marketState, vwap, fastEma, slowEma);
                if (directional.Direction is not null)
                {
                    candidate = new(directional.Direction.Value,
                        directional.Confidence,
                        DirectionalParticipationPolicy.SensexStrategy,
                        directional.Reasons);
                    supplemental = true;
                }
            }
        }
        if (!supplemental)
        {
            var directional = DirectionalParticipationPolicy.Evaluate(bars,
                marketState, vwap, fastEma, slowEma);
            if (!hasActivePosition && directional.Direction is not null &&
                (candidate.Direction is null ||
                 candidate.Direction == directional.Direction))
            {
                candidate = new(directional.Direction.Value,
                    directional.Confidence,
                    DirectionalParticipationPolicy.SensexStrategy,
                    directional.Reasons);
                supplemental = true;
            }
        }
        if (candidate.Strategy == "Sensex controlled early pullback" &&
            candidate.Direction is { } earlyDirection &&
            SensexFailedBreakoutEntryPolicy.Rejection(bars, earlyDirection, marketState) is { } rejection)
            candidate = candidate with { Direction = null, Reasons = [.. candidate.Reasons, rejection] };
        if (candidate.Strategy == "Sensex range-edge rejection" && candidate.Direction is { } rangeDirection &&
            SensexFailedBreakoutEntryPolicy.OppositeRangeRejection(bars, rangeDirection, marketState) is { } rangeRejection)
            candidate = candidate with { Direction = null, Reasons = [.. candidate.Reasons, rangeRejection] };
        if (candidate.Strategy == DirectionalParticipationPolicy.SensexStrategy &&
            candidate.Direction is { } participationDirection)
        {
            var confirmation = SensexMaturePullbackConfirmationPolicy.Evaluate(
                bars, participationDirection, marketState.MoveMaturityAtr);
            candidate = candidate with
            {
                Direction = confirmation.Permitted ? candidate.Direction : null,
                Reasons = [.. candidate.Reasons, .. confirmation.Reasons]
            };
            // Do not fall back to another candidate on this same candle after
            // rejecting its selected pullback. The reason reaches NoSignal audit.
        }
        // Retain the broader thesis through a strong move, but distinguish that
        // from a safe new entry. Do not fall back to a different path on the same
        // candle after rejecting the selected persistent entry for extension.
        // This applies only to Sensex; Nifty and existing positions are unchanged.
        if (persistent && candidate.Direction is not null)
        {
            var extension = PersistentDirectionalThesisAnalyzerV1.EntryExtensionAtr(bars);
            if (extension is null || extension > MaximumPersistentEntryExtensionAtr)
                candidate = candidate with { Direction = null, Reasons = [.. candidate.Reasons,
                    $"Sensex persistent entry is {extension:F2} ATR from EMA9 (limit {MaximumPersistentEntryExtensionAtr:F2}). Retain the thesis and wait for a fresh pullback confirmation within the entry budget."] };
        }
        return new(candidate, supplemental, persistent,
            earlyCandidate, earlyAssessment);
    }
}
