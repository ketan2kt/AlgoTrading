using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Execution;

// A short rolling window becoming less choppy is not confirmation of a reversal
// against the five-minute direction. Aligned trend entries are untouched.
public static class NiftyCountertrendEntryPolicy
{
    public const string Version = "nifty-countertrend-confirmation-v1";

    public static MiddayChopConfirmationDecision Evaluate(IReadOnlyList<StrategyPriceBar> completedBars,
        Direction direction, MultiTimeframeDecisionAssessment timeframes, int intervalSeconds)
    {
        if (!timeframes.Timeframes.Any(frame => frame.Timeframe == "5m" &&
                frame.Direction is not null && frame.Direction != direction))
            return new(false, true, ["The five-minute direction does not oppose this entry."]);
        var confirmation = MiddayChopConfirmationPolicy.ConfirmCompletedStructure(completedBars, direction, intervalSeconds);
        return confirmation with { Reasons = confirmation.Permitted ? confirmation.Reasons :
            ["Five-minute direction opposes the entry; wait for a completed structure break or pullback resumption, not only a one-minute rebound."] };
    }
}
