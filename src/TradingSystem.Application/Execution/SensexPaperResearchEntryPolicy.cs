using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Execution;

public sealed record SensexPaperResearchEntryDecision(bool Permitted,
    decimal EffectiveConfidence, IReadOnlyList<string> Reasons);

public static class SensexPaperResearchEntryPolicy
{
    public const decimal MinimumConfidence = .70m;

    public static SensexEarlyPullbackDecision DeriveLifecycleCandidate(
        SensexSetupLifecycle lifecycle)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        if (lifecycle.Phase != SensexSetupPhase.EntryWindow || lifecycle.Direction is null ||
            lifecycle.AtrExtension is < -.90m or > .55m || lifecycle.EmaSeparationAtr < .20m)
            return new(null, .50m,
                ["The unified Sensex lifecycle does not contain a qualified early entry window."]);
        var confidence = Math.Clamp(.66m + lifecycle.EmaSeparationAtr * .12m, .70m, .78m);
        return new(lifecycle.Direction, confidence,
            ["The unified lifecycle confirms a completed pullback rejection.",
             $"Entry remains within the directional trigger budget at {lifecycle.AtrExtension:F2} ATR.",
             $"EMA separation is {lifecycle.EmaSeparationAtr:F2} ATR."]);
    }

    public static SensexPaperResearchEntryDecision Evaluate(Direction? candidateDirection,
        decimal confidence, SensexSetupLifecycle lifecycle, bool hasActivePosition,
        PersistentDirectionalThesisV1? persistentThesis = null)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        var reasons = new List<string>();
        if (candidateDirection is null) reasons.Add("The early-pullback candidate has no direction.");
        var thesisAligned = candidateDirection is not null &&
                            persistentThesis is { ChangeOfCharacter: false, Direction: not null } &&
                            persistentThesis.Direction == candidateDirection &&
                            persistentThesis.Confidence >= .68m;
        var effectiveConfidence = thesisAligned
            ? Math.Clamp(confidence * .55m + persistentThesis!.Confidence * .45m, 0m, .95m)
            : confidence;
        if (effectiveConfidence < MinimumConfidence)
            reasons.Add($"Composite confidence {effectiveConfidence:P0} is below the {MinimumConfidence:P0} research threshold.");
        if (lifecycle.Phase != SensexSetupPhase.EntryWindow)
            reasons.Add($"Lifecycle phase {lifecycle.Phase} is not a completed pullback-rejection entry window.");
        if (candidateDirection is not null && lifecycle.Direction != candidateDirection)
            reasons.Add("Candidate direction conflicts with the lifecycle direction.");
        var minimumExtension = thesisAligned ? -1.25m : -.90m;
        var maximumExtension = thesisAligned ? .65m : .55m;
        if (lifecycle.AtrExtension < minimumExtension || lifecycle.AtrExtension > maximumExtension)
            reasons.Add($"Candidate location is {lifecycle.AtrExtension:F2} ATR from its trigger; the current directional budget is {minimumExtension:F2} to {maximumExtension:F2} ATR.");
        if (lifecycle.EmaSeparationAtr < .20m)
            reasons.Add("EMA separation is insufficient for a directional research entry.");
        if (hasActivePosition)
            reasons.Add("Another Sensex paper position is already active.");
        return new(reasons.Count == 0, effectiveConfidence, reasons.Count == 0
            ? ["Composite session and pullback evidence qualifies for controlled paper research only.",
               thesisAligned ? "The persistent directional thesis confirms the candidate direction." : "The lifecycle candidate qualifies without a thesis override."]
            : reasons);
    }
}
