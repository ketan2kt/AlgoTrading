using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Execution;

// The exact ordered post-signal, pre-OI gates used by Nifty and Sensex production
// and replay. The stable class name is retained for existing callers.
// The caller supplies completed, as-of bars; this function never reads
// a clock, database, broker or original trade history.
public sealed record NiftyPreQuoteEntryGateDecision(bool Permitted,
    string RejectionOutcome, IReadOnlyList<string> Reasons,
    string LocationContext, MarketLocationDecision? Location);

public static class NiftyPreQuoteEntryGatePolicy
{
    public static NiftyPreQuoteEntryGateDecision Evaluate(
        TimeOnly localTime, IReadOnlyList<StrategyPriceBar> completedSession,
        IReadOnlyList<StrategyPriceBar> previousSessions,
        Direction direction, MarketStructureQualitySnapshot structure,
        MultiTimeframeDecisionAssessment timeframes,
        int candleIntervalSeconds, decimal atrPoints,
        decimal openingRangeHigh, decimal openingRangeLow)
    {
        var midday = MiddayChopConfirmationPolicy.Evaluate(localTime,
            completedSession, direction, structure, candleIntervalSeconds, includeOtherHours: true);
        if (!midday.Permitted)
            return new(false, "ChopConfirmationRejected", midday.Reasons,
                "", null);

        var location = MarketLocationPolicy.Evaluate(completedSession,
            previousSessions, direction, atrPoints, openingRangeHigh,
            openingRangeLow);
        location = ConfirmedEntryLocationPolicy.Evaluate(location, completedSession,
            direction, structure, timeframes, atrPoints, candleIntervalSeconds);
        if (!location.Permitted)
            return new(false, "MarketLocationRejected", location.Reasons,
                location.Context, location);

        var move = SessionMoveQualityPolicy.Evaluate(completedSession,
            direction, atrPoints);
        if (!move.Permitted)
            return new(false, "SessionMoveMaturityRejected", move.Reasons,
                location.Context, location);

        var mature = MatureEdgeEntryPolicy.Evaluate(move, timeframes, direction,
            completedSession, candleIntervalSeconds);
        return new(mature.Permitted,
            mature.Permitted ? "" : "MatureEdgeConfirmationRejected",
            mature.Reasons, location.Context, location);
    }
}
