namespace TradingSystem.Application.Diagnostics;

// Count explicit gate outcomes, never infer a rejection from the absence of an
// acceptance. EntryStage is the Nifty audit mirror of StrategyEvaluation.
public static class DailyDecisionClassification
{
    public enum Kind { Accepted, Rejected, NoSetup, Informational, Mirror }

    public static Kind Classify(string outcome)
    {
        if (outcome.StartsWith("EntryStage:", StringComparison.Ordinal)) return Kind.Mirror;
        if (outcome.Contains(':', StringComparison.Ordinal)) return Kind.Informational;
        if (outcome is "NoSignal" or "NoSetup") return Kind.NoSetup;
        if (outcome is "PaperPositionOpened" or "OptionOpenInterestAccepted" or "SignalGenerated" or "SignalAccepted")
            return Kind.Accepted;
        if (outcome.EndsWith("Rejected", StringComparison.Ordinal) || outcome is
            "DataIncomplete" or "OptionOpenInterestUnavailable" or "DuplicateActiveContract" or
            "ActivePosition" or "RiskBlocked" or "InsufficientCapital") return Kind.Rejected;
        return Kind.Informational;
    }
}
