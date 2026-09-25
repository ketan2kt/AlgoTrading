using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Execution;

public sealed record SensexPaperResearchEntryDecision(bool Permitted,
    IReadOnlyList<string> Reasons);

public static class SensexPaperResearchEntryPolicy
{
    public const decimal MinimumConfidence = .70m;
    public const int MaximumEntriesPerDay = 2;

    public static SensexPaperResearchEntryDecision Evaluate(Direction? candidateDirection,
        decimal confidence, SensexSetupLifecycle lifecycle, int entriesToday,
        bool hasActivePosition)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        var reasons = new List<string>();
        if (candidateDirection is null) reasons.Add("The early-pullback candidate has no direction.");
        if (confidence < MinimumConfidence)
            reasons.Add($"Candidate confidence {confidence:P0} is below the {MinimumConfidence:P0} research threshold.");
        if (lifecycle.Phase != SensexSetupPhase.EntryWindow)
            reasons.Add($"Lifecycle phase {lifecycle.Phase} is not a completed pullback-rejection entry window.");
        if (candidateDirection is not null && lifecycle.Direction != candidateDirection)
            reasons.Add("Candidate direction conflicts with the lifecycle direction.");
        if (lifecycle.AtrExtension is < -.35m or > .35m)
            reasons.Add($"Candidate location is {lifecycle.AtrExtension:F2} ATR from its trigger; controlled research requires -0.35 to +0.35 ATR.");
        if (lifecycle.EmaSeparationAtr < .30m)
            reasons.Add("EMA separation is insufficient for a directional research entry.");
        if (entriesToday >= MaximumEntriesPerDay)
            reasons.Add($"The daily limit of {MaximumEntriesPerDay} controlled Sensex research entries is reached.");
        if (hasActivePosition)
            reasons.Add("Another Sensex paper position is already active.");
        return new(reasons.Count == 0, reasons.Count == 0
            ? ["High-confidence pullback rejection qualifies for controlled paper research only."]
            : reasons);
    }
}
