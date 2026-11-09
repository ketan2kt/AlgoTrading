using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Execution;

public sealed record MatureEdgeEntryDecision(bool Permitted, IReadOnlyList<string> Reasons);

/// <summary>
/// A mature move near its session extreme needs convincing immediate participation.
/// This is a narrow confirmation check, not a general cap on directional entries.
/// </summary>
public static class MatureEdgeEntryPolicy
{
    public static MatureEdgeEntryDecision Evaluate(SessionMoveQualityDecision move,
        MultiTimeframeDecisionAssessment timeframes, Direction direction,
        IReadOnlyList<StrategyPriceBar> completedSession, int candleIntervalSeconds = 60,
        bool relaxedNiftyEntries = false)
    {
        if (move.PeakMoveAtr < (relaxedNiftyEntries ? 10m : 8m) || move.DistanceFromExtremeAtr > .75m)
            return new(true, ["The candidate is not a mature near-extreme entry."]);

        // The 1m timeframe's Strength describes the entire supplied session.
        // Immediate participation must instead use the latest completed window:
        // morning chop cannot veto a fresh resumption for the rest of the day,
        // and an efficient morning cannot disguise a currently weakening move.
        var recent = completedSession.TakeLast(5).ToArray();
        var contiguous = recent.Length == 5 && candleIntervalSeconds > 0 &&
            recent.Zip(recent.Skip(1)).All(pair =>
                pair.Second.OpenTimeUtc - pair.First.OpenTimeUtc == TimeSpan.FromSeconds(candleIntervalSeconds));
        var travelled = recent.Zip(recent.Skip(1), (left, right) => Math.Abs(right.Close - left.Close)).Sum();
        var net = recent.Length == 0 ? 0m : recent[^1].Close - recent[0].Close;
        var strength = travelled <= 0m ? 0m : Math.Abs(net) / travelled;
        var aligned = recent.Length >= 2 && (direction == Direction.Buy
            ? net > 0m && recent[^1].Close > recent[^1].Open && recent[^1].Close > recent[^2].Close
            : net < 0m && recent[^1].Close < recent[^1].Open && recent[^1].Close < recent[^2].Close);
        if (contiguous && aligned && strength >= (relaxedNiftyEntries ? .35m : .45m) && timeframes.CandidateAligned)
            return new(true, [$"A mature near-extreme entry has fresh five-bar participation ({strength:P0}) and multi-timeframe alignment."]);

        return new(false,
            [$"The same-direction move is {move.PeakMoveAtr:F2} ATR mature and only " +
             $"{move.DistanceFromExtremeAtr:F2} ATR from its extreme; fresh five-bar " +
             $"participation ({strength:P0}), contiguous data or timeframe alignment is insufficient."]);
    }
}
