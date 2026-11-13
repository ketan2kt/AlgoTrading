using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Execution;

public sealed record SensexPullbackConfirmation(bool Permitted, bool Applies,
    decimal? RoomAtr, decimal? ConfirmationLevel, IReadOnlyList<string> Reasons);

// Additional confirmation for the directional-participation path only. Callers
// supply completed, ascending, current-session bars as of the decision clock.
// Reuses the existing 4.25 ATR maturity budget and 0.5 ATR limited-room warning;
// neither threshold was fitted to the November 5 losing trade.
public static class SensexMaturePullbackConfirmationPolicy
{
    public const string Version = "sensex-mature-pullback-v2";

    public static SensexPullbackConfirmation Evaluate(
        IReadOnlyList<StrategyPriceBar> bars, Direction direction, decimal moveMaturityAtr)
    {
        ArgumentNullException.ThrowIfNull(bars);
        if (direction is not (Direction.Buy or Direction.Sell))
            throw new ArgumentOutOfRangeException(nameof(direction));
        if (moveMaturityAtr <= 4.25m)
            return new(true, false, null, null, []);
        if (bars.Count < 15)
            return new(false, true, null, null,
                ["Mature pullback confirmation requires fifteen completed candles."]);

        var recent = bars.TakeLast(15).ToArray();
        var latest = recent[^1];
        var atr = recent.Skip(1).Select((bar, index) => Math.Max(bar.High - bar.Low,
            Math.Max(Math.Abs(bar.High - recent[index].Close),
                Math.Abs(bar.Low - recent[index].Close)))).Average();
        if (atr <= 0m)
            return new(false, true, null, null, ["Mature pullback confirmation requires valid ATR."]);

        // Equality is an unbroken level, not evidence of unlimited room.
        var opposing = direction == Direction.Buy
            ? bars.SkipLast(1).Where(bar => bar.High >= latest.Close)
                .Select(bar => (decimal?)bar.High).Min()
            : bars.SkipLast(1).Where(bar => bar.Low <= latest.Close)
                .Select(bar => (decimal?)bar.Low).Max();
        var room = opposing is null ? (decimal?)null : Math.Abs(opposing.Value - latest.Close) / atr;
        if (room is null or >= .5m)
            return new(true, false, room, null, []);

        // Use the same four preceding bars as the original pullback detector.
        // A wick through the level or merely a favourable close is insufficient.
        var pullback = recent.TakeLast(5).SkipLast(1).ToArray();
        var level = direction == Direction.Buy
            ? pullback.Max(bar => bar.High) : pullback.Min(bar => bar.Low);
        var confirmed = direction == Direction.Buy
            ? latest.Close > latest.Open && latest.Close > level
            : latest.Close < latest.Open && latest.Close < level;
        // Clearing the pullback does not clear a separate level beyond it.
        // Re-evaluate on the next candidate; do not promote a smaller local
        // break while the remaining room is still below the existing budget.
        return new(false, true, room, level,
            [$"Mature pullback: {moveMaturityAtr:F2} ATR move, only {room:F2} ATR room to " +
             $"{(direction == Direction.Buy ? "resistance" : "support")} {opposing:F2}. " +
             (confirmed ? "The local structure break does not clear that level. Wait for room to reopen."
                : $"Waiting for structure confirmation beyond the pullback level {level:F2}.")]);
    }
}
