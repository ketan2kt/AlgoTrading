using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Execution;

// A shared, pure slice of the production Bank Nifty entry path. Both the
// live-clock paper engine and historical adapter call this exact rule; the
// caller remains responsible for as-of inputs, OI, contract and quote gates.
public sealed record BankIndexPreQuoteGateDecision(
    bool Permitted, string RejectionOutcome, IReadOnlyList<string> Reasons,
    MarketLocationDecision Location,
    SessionMoveQualityDecision? Move,
    MatureEdgeEntryDecision? MatureEdge);

public static class BankIndexPreQuoteGatePolicy
{
    public static BankIndexPreQuoteGateDecision Evaluate(
        IReadOnlyList<StrategyPriceBar> completedSession,
        IReadOnlyList<StrategyPriceBar> previousSession,
        Direction direction, decimal atr,
        decimal openingRangeHigh, decimal openingRangeLow, int candleIntervalSeconds = 60)
    {
        var chop = MiddayChopConfirmationPolicy.EvaluateObservedPath(completedSession,
            direction, candleIntervalSeconds);
        var location = MarketLocationPolicy.Evaluate(completedSession,
            previousSession, direction, atr, openingRangeHigh, openingRangeLow);
        if (!chop.Permitted)
            return new(false, "ChopConfirmationRejected", chop.Reasons, location, null, null);
        if (!location.Permitted)
            return new(false, "MarketLocationRejected", location.Reasons,
                location, null, null);

        var move = SessionMoveQualityPolicy.Evaluate(completedSession, direction, atr);
        var mature = MatureEdgeEntryPolicy.Evaluate(move,
            MultiTimeframeDecisionPolicy.Analyze(completedSession, direction,
                openingRangeHigh, openingRangeLow), direction);
        return new(move.Permitted && mature.Permitted,
            move.Permitted && mature.Permitted ? "" : "SessionMoveQualityRejected",
            [.. move.Reasons, .. mature.Reasons], location, move, mature);
    }
}
