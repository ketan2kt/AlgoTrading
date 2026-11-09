using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Strategies;

public sealed record PaperEntryQualityDecision(bool Permitted, IReadOnlyList<string> Reasons);

public static class PaperEntryQualityPolicy
{
    public static PaperEntryQualityDecision Evaluate(
        MarketStructureQualitySnapshot structure,
        Direction candidateDirection,
        string strategyId = "",
        bool strongRegimeAligned = false,
        decimal regimeConfidence = 0m,
        decimal relativeVolume = 0m,
        bool relaxedNiftyEntries = false)
    {
        ArgumentNullException.ThrowIfNull(structure);
        var reasons = new List<string>();

        if (relaxedNiftyEntries)
        {
            var standard = Evaluate(structure, candidateDirection, strategyId,
                strongRegimeAligned, regimeConfidence, relativeVolume);
            if (standard.Permitted) return standard;
            if (structure.State == MarketStructureQualityState.Unavailable)
                reasons.Add("Structure-quality inputs are unavailable.");
            if (structure.TrendEfficiency < .25m || structure.ChopScore > .65m ||
                structure.SwingConsistency < .35m)
                reasons.Add("Trial requires efficiency >=25%, chop <=65% and swing consistency >=35%.");
            if (structure.MoveMaturityAtr > 6m)
                reasons.Add("Trial move maturity exceeds 6 ATR.");
            if (structure.RoomToRiskRatio is < .60m)
                reasons.Add("Trial requires at least 0.60R of known structural room.");
            if (structure.ObservedBias is { } trialBias && trialBias != candidateDirection)
                reasons.Add($"Entry direction {candidateDirection} conflicts with short-term structure {trialBias}.");
            return new(reasons.Count == 0, reasons.Count == 0
                ? ["Broader Nifty entry trial structure thresholds passed."] : reasons.ToArray());
        }

        var balancedContinuation = strongRegimeAligned && regimeConfidence >= .65m &&
            relativeVolume >= .75m && structure.ObservedBias == candidateDirection &&
            structure.State is (MarketStructureQualityState.DevelopingTrend or
                MarketStructureQualityState.VolatilityTransition) &&
            structure.RoomToRiskRatio is >= .50m;

        if (!structure.WouldPermit)
            reasons.AddRange(structure.Reasons.Where(reason =>
                reason.Contains("chop", StringComparison.OrdinalIgnoreCase) ||
                reason.Contains("mature", StringComparison.OrdinalIgnoreCase) ||
                reason.Contains("conflicts", StringComparison.OrdinalIgnoreCase) ||
                (!balancedContinuation && reason.Contains("room", StringComparison.OrdinalIgnoreCase)) ||
                reason.Contains("twelve", StringComparison.OrdinalIgnoreCase)));

        var volatilityBreakout = structure.State == MarketStructureQualityState.VolatilityTransition &&
            (strategyId.Contains("opening-range-breakout", StringComparison.Ordinal) ||
             strategyId.Contains("momentum-expansion", StringComparison.Ordinal));
        if (structure.State is not (MarketStructureQualityState.CleanTrend or
            MarketStructureQualityState.DevelopingTrend) && !volatilityBreakout &&
            !balancedContinuation)
            reasons.Add($"Entry blocked in {structure.State}; a clean or developing trend is required.");

        if (structure.ObservedBias is { } bias && bias != candidateDirection)
            reasons.Add($"Entry direction {candidateDirection} conflicts with short-term structure {bias}.");

        return new(reasons.Count == 0, reasons.Distinct(StringComparer.Ordinal).ToArray());
    }
}
