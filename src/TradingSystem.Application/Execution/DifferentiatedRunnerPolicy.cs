namespace TradingSystem.Application.Execution;

// Only an already reduced, profitable normal position may use the runner floor.
// Callers must supply fresh, completed-candle continuation evidence as of now.
// No state is latched: loss of confirmation restores the ordinary protection.
public static class DifferentiatedRunnerPolicy
{
    public const decimal PeakGainLockFraction = .50m;

    public static bool Eligible(bool enabled, int originalQuantity, int remainingQuantity,
        bool partialFilled, bool continuationConfirmed) =>
        enabled && partialFilled && originalQuantity > 0 && remainingQuantity > 0 &&
        remainingQuantity < originalQuantity && continuationConfirmed;
}
