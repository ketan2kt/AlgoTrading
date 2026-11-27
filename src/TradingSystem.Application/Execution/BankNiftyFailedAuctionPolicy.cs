using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Execution;

/// <summary>
/// A new session extreme immediately rejected through the whole breakout candle
/// remains an auction to resolve, not permission to repeat a smaller local break.
/// Reconstruct from completed candles only; no order, exit or mutable state changes.
/// </summary>
public static class BankNiftyFailedAuctionPolicy
{
    public const string Version = "bank-failed-auction-v1";

    public static string? Rejection(IReadOnlyList<StrategyPriceBar> source,
        Direction direction, int intervalSeconds = 60)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (direction is not (Direction.Buy or Direction.Sell))
            throw new ArgumentOutOfRangeException(nameof(direction));
        if (intervalSeconds is not (60 or 300)) return null;
        // A gap or a new session cannot supply an inferred failure/reclaim.
        // Existing data-readiness gates remain responsible for missing history.
        var bars = SharedMarketContextPolicy.CompletedTail(source, intervalSeconds);
        if (bars.Length < 14) return null;
        var sign = direction == Direction.Buy ? 1m : -1m;
        foreach (var failedSide in new[] { Direction.Buy, Direction.Sell })
        {
            var failedSign = failedSide == Direction.Buy ? 1m : -1m;
            var level = Extreme(bars.Take(12), failedSide);
            for (var i = 12; i < bars.Length - 1; i++)
            {
                var impulse = bars[i];
                var failure = bars[i + 1];
                var priorLevel = level;
                level = Extend(level, impulse, failedSide);
                var boundary = failedSide == Direction.Buy ? impulse.Low : impulse.High;
                var extreme = failedSide == Direction.Buy ? impulse.High : impulse.Low;
                // Require an actual close outside the prior session extreme and
                // a subsequent close through the opposite candle boundary.
                // Neither an extreme wick nor an ordinary pullback is a failure.
                if (failedSign * (impulse.Close - impulse.Open) <= 0m ||
                    failedSign * (impulse.Close - priorLevel) <= 0m ||
                    failedSign * (failure.Close - boundary) >= 0m) continue;
                if (bars.Skip(i + 1).Any(b => failedSign * (b.Close - extreme) > 0m)) continue;
                if (failedSide == direction)
                {
                    if (sign * (bars[^1].Close - boundary) <= 0m)
                        return "The session-extreme breakout was fully rejected; wait for a completed close reclaiming the failed candle's near boundary before retrying that direction.";
                    continue;
                }

                // Reuse two-close/three-bar acceptance, wholly after the failure.
                // Do not replace a deferred trade with an unconfirmed opposite
                // impulse, or reuse acceptance after a later close loses its level.
                decimal? acceptedLevel = null;
                if (i + 5 >= bars.Length)
                    return "The opposite-side entry has no accepted post-failure range break yet.";
                var reverseLevel = Extreme(bars.Skip(i + 1).Take(3), direction);
                for (var j = i + 5; j < bars.Length; j++)
                {
                    if (acceptedLevel is { } accepted && sign * (bars[j].Close - accepted) < 0m)
                        acceptedLevel = null;
                    if (sign * (bars[j - 1].Close - reverseLevel) > 0m &&
                        sign * (bars[j].Close - reverseLevel) > 0m)
                        acceptedLevel = reverseLevel;
                    reverseLevel = Extend(reverseLevel, bars[j - 1], direction);
                }
                if (acceptedLevel is null)
                    return "The opposite-side entry needs two completed closes beyond the post-failure range with that acceptance level still intact.";
            }
        }
        return null;
    }

    private static decimal Extreme(IEnumerable<StrategyPriceBar> bars, Direction side) =>
        side == Direction.Buy ? bars.Max(b => b.High) : bars.Min(b => b.Low);

    private static decimal Extend(decimal level, StrategyPriceBar bar, Direction side) =>
        side == Direction.Buy ? Math.Max(level, bar.High) : Math.Min(level, bar.Low);
}
