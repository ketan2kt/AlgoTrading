using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Execution;

public static class ConfirmedEntryLocationPolicy
{
    private const string MiddleReason = "Price is in the middle of the current session range; location offers no clear edge.";

    public static MarketLocationDecision Evaluate(MarketLocationDecision location,
        IReadOnlyList<StrategyPriceBar> bars, Direction direction,
        MarketStructureQualitySnapshot structure, MultiTimeframeDecisionAssessment timeframes,
        decimal atr, int candleIntervalSeconds = 60, bool relaxedNiftyEntries = false)
    {
        ArgumentNullException.ThrowIfNull(location);
        var eligibleStructure = structure.WouldPermit &&
            structure.State is (MarketStructureQualityState.CleanTrend or MarketStructureQualityState.DevelopingTrend) ||
            relaxedNiftyEntries && PaperEntryQualityPolicy.Evaluate(structure, direction,
                relaxedNiftyEntries: true).Permitted;
        if (location.Permitted || location.Reasons.Count != 1 || location.Reasons[0] != MiddleReason ||
            bars.Count < 2 || atr <= 0m || !eligibleStructure ||
            structure.ObservedBias != direction || !timeframes.CandidateAligned)
            return location;
        var last = bars[^1];
        var alignedClose = direction == Direction.Buy
            ? last.Close > last.Open && last.Close > bars[^2].Close
            : last.Close < last.Open && last.Close < bars[^2].Close;
        var immediateBreak = direction == Direction.Buy ? last.Close > bars[^2].High : last.Close < bars[^2].Low;
        var trigger = alignedClose && (immediateBreak ||
            MiddayChopConfirmationPolicy.ConfirmCompletedStructure(bars, direction, candleIntervalSeconds).Permitted);
        var room = direction == Direction.Buy ? location.NearestResistance - last.Close :
            last.Close - location.NearestSupport;
        // Only the coarse range-middle veto can be replaced. All actual
        // support/resistance, maturity, OI, timing and risk gates still apply.
        return trigger && room >= atr * (relaxedNiftyEntries ? .80m : 1.10m)
            ? location with { Permitted = true, Context = location.Context + "+ConfirmedContinuation",
                Reasons = ["A completed direction-aligned continuation with multi-timeframe alignment and structural room qualifies inside the session range."] }
            : location;
    }
}
