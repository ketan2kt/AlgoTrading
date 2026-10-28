namespace TradingSystem.Infrastructure.Execution;

public static class MarketPaperExitSequencingPolicy
{
    public static bool ShouldTakePartial(bool manualExitRequested, bool stopHit,
        bool targetHit, bool continuationConfirmed, bool forced) =>
        !manualExitRequested && !stopHit && !forced && (!targetHit || continuationConfirmed);
}
