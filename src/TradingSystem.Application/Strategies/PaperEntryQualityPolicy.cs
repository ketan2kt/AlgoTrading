using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Strategies;

public sealed record PaperEntryQualityDecision(bool Permitted, IReadOnlyList<string> Reasons);

public static class PaperEntryQualityPolicy
{
    public static PaperEntryQualityDecision Evaluate(
        MarketStructureQualitySnapshot structure,
        Direction candidateDirection)
    {
        ArgumentNullException.ThrowIfNull(structure);
        var reasons = new List<string>();

        if (!structure.WouldPermit)
            reasons.AddRange(structure.Reasons.Where(reason =>
                reason.Contains("chop", StringComparison.OrdinalIgnoreCase) ||
                reason.Contains("mature", StringComparison.OrdinalIgnoreCase) ||
                reason.Contains("conflicts", StringComparison.OrdinalIgnoreCase) ||
                reason.Contains("room", StringComparison.OrdinalIgnoreCase) ||
                reason.Contains("twelve", StringComparison.OrdinalIgnoreCase)));

        if (structure.State is not (MarketStructureQualityState.CleanTrend or
            MarketStructureQualityState.DevelopingTrend))
            reasons.Add($"Entry blocked in {structure.State}; a clean or developing trend is required.");

        if (structure.ObservedBias is { } bias && bias != candidateDirection)
            reasons.Add($"Entry direction {candidateDirection} conflicts with short-term structure {bias}.");

        return new(reasons.Count == 0, reasons.Distinct(StringComparer.Ordinal).ToArray());
    }
}
