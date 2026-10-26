using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Execution;

public sealed record SessionMoveQualityDecision(bool Permitted, decimal PeakMoveAtr,
    decimal CurrentMoveAtr, decimal DistanceFromExtremeAtr, IReadOnlyList<string> Reasons);

/// <summary>
/// Prevents chasing a mature session impulse at its extreme while leaving fresh pullback
/// and continuation structures eligible. It intentionally has no index-specific point cutoffs.
/// </summary>
public static class SessionMoveQualityPolicy
{
    public const decimal MatureMoveAtr = 2.5m;
    public const decimal NearExtremeAtr = .35m;
    public const decimal RequiredPullbackAtr = .35m;

    public static SessionMoveQualityDecision Evaluate(
        IReadOnlyList<StrategyPriceBar> fullSession,
        Direction direction,
        decimal atr)
    {
        ArgumentNullException.ThrowIfNull(fullSession);
        if (fullSession.Count < 4 || atr <= 0m)
            return new(true, 0m, 0m, 0m,
                ["Full-session move assessment is unavailable; existing entry checks remain in force."]);

        var open = fullSession[0].Open;
        var latest = fullSession[^1];
        var peakMove = direction == Direction.Buy
            ? fullSession.Max(value => value.High) - open
            : open - fullSession.Min(value => value.Low);
        var currentMove = direction == Direction.Buy
            ? latest.Close - open
            : open - latest.Close;
        var sessionExtreme = direction == Direction.Buy
            ? fullSession.Max(value => value.High)
            : fullSession.Min(value => value.Low);
        var distanceFromExtreme = direction == Direction.Buy
            ? sessionExtreme - latest.Close
            : latest.Close - sessionExtreme;
        var peakMoveAtr = Math.Max(0m, peakMove / atr);
        var currentMoveAtr = currentMove / atr;
        var distanceFromExtremeAtr = Math.Max(0m, distanceFromExtreme / atr);

        if (currentMove <= 0m || peakMoveAtr < MatureMoveAtr ||
            distanceFromExtremeAtr > NearExtremeAtr)
            return new(true, peakMoveAtr, currentMoveAtr, distanceFromExtremeAtr,
                ["The candidate is not chasing a mature same-direction session extreme."]);

        var recent = fullSession.TakeLast(4).ToArray();
        var pullbackBars = recent.SkipLast(1).ToArray();
        var pullbackObserved = direction == Direction.Buy
            ? pullbackBars.Any(value => value.Low <= sessionExtreme - atr * RequiredPullbackAtr)
            : pullbackBars.Any(value => value.High >= sessionExtreme + atr * RequiredPullbackAtr);
        var resumed = direction == Direction.Buy
            ? latest.Close > latest.Open && latest.Close > pullbackBars.Max(value => value.High)
            : latest.Close < latest.Open && latest.Close < pullbackBars.Min(value => value.Low);

        return pullbackObserved && resumed
            ? new(true, peakMoveAtr, currentMoveAtr, distanceFromExtremeAtr,
                ["A mature session move has a completed pullback and direction-aligned resumption trigger."])
            : new(false, peakMoveAtr, currentMoveAtr, distanceFromExtremeAtr,
                [$"Same-direction session move reached {peakMoveAtr:F2} ATR and price is within " +
                 $"{distanceFromExtremeAtr:F2} ATR of its extreme; wait for a completed pullback and resumption."]);
    }
}
