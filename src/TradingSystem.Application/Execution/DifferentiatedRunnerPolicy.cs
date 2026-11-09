namespace TradingSystem.Application.Execution;

// A profitable, structurally confirmed normal position may qualify before the
// first partial exit. Never widen an already persisted/broker-confirmed stop.
// Callers must supply fresh, completed-candle continuation evidence as of now.
// No state is latched: loss of confirmation restores the ordinary protection.
public static class DifferentiatedRunnerPolicy
{
    public const decimal PeakGainLockFraction = .50m;
    public const decimal MinimumUnreducedFavourableR = .50m;
    public const string Version = "current-structure-runner-v2";

    public static bool Eligible(bool enabled, int originalQuantity, int remainingQuantity,
        bool partialFilled, bool continuationConfirmed, decimal favourableR = 0m) =>
        enabled && partialFilled && originalQuantity > 0 && remainingQuantity > 0 &&
        remainingQuantity < originalQuantity && continuationConfirmed ||
        enabled && !partialFilled && originalQuantity > 0 && remainingQuantity == originalQuantity &&
        continuationConfirmed && favourableR >= MinimumUnreducedFavourableR;

    public static bool AllowsTargetExtension(bool originalPlanAllows,
        bool currentStructureConfirmed) => originalPlanAllows || currentStructureConfirmed;
}
