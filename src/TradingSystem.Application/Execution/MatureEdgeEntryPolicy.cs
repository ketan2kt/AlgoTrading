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
        MultiTimeframeDecisionAssessment timeframes, Direction direction)
    {
        if (move.PeakMoveAtr < 8m || move.DistanceFromExtremeAtr > .75m)
            return new(true, ["The candidate is not a mature near-extreme entry."]);

        var immediate = timeframes.Timeframes.FirstOrDefault(value => value.Timeframe == "1m");
        if (immediate is not null && immediate.Direction == direction &&
            immediate.Strength >= .45m && timeframes.CandidateAligned)
            return new(true, ["A mature near-extreme entry has fresh one-minute participation and multi-timeframe alignment."]);

        return new(false,
            [$"The same-direction move is {move.PeakMoveAtr:F2} ATR mature and only " +
             $"{move.DistanceFromExtremeAtr:F2} ATR from its extreme; fresh one-minute " +
             "participation is too weak for a near-extreme entry."]);
    }
}
