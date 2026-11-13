using System.Globalization;
using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Execution;

public sealed record BankNiftyEntryStructureDecision(bool Permitted, string? Rejection,
    IReadOnlyList<string> Reasons);

public static class BankNiftyEntryStructurePolicy
{
    public const string Version = "bank-entry-structure-v2";
    public const string OpposingLevel = "BankNiftyCountertrendLevel";
    public const string MarginalBreak = "BankNiftyMarginalBreak";
    public const string FailedFlip = "BankNiftyFailedFlipUnconfirmed";
    public const string FailedRetry = "BankNiftyFailedRetryUnconfirmed";
    public const string ExtendedImpulse = "BankNiftyExtendedImpulse";
    private const decimal CompactRetryBaseAtr = 1.5m;
    private const decimal ExtendedImpulseAtr = 3m;
    private const decimal MinimumImpulseAcceleration = 1.5m;
    private static readonly BankNiftyEntryStructureDecision Allowed = new(true, null, []);

    public static BankNiftyEntryStructureDecision Evaluate(IReadOnlyList<StrategyPriceBar> bars,
        Direction direction, decimal atr, int intervalSeconds, DateTimeOffset asOf,
        DateTimeOffset? oppositeFailedExit = null, DateTimeOffset? sameSideFailedExit = null)
    {
        ArgumentNullException.ThrowIfNull(bars);
        if (direction is not (Direction.Buy or Direction.Sell))
            throw new ArgumentOutOfRangeException(nameof(direction));
        var recentOppositeFailure = BankNiftyFailedSetupCooldownPolicy.Blocks(asOf, oppositeFailedExit);
        var sameSideFailure = sameSideFailedExit is { } failed && failed <= asOf;
        if (intervalSeconds is not (60 or 300) || bars.Count == 0)
            return recentOppositeFailure ? WaitForFailedFlip() : sameSideFailure ? WaitForFailedRetry() : Allowed;

        var interval = TimeSpan.FromSeconds(intervalSeconds);
        var completed = bars.Where(b => b.OpenTimeUtc.Add(interval) <= asOf).ToArray();
        if (completed.Length == 0)
            return recentOppositeFailure ? WaitForFailedFlip() : sameSideFailure ? WaitForFailedRetry() : Allowed;
        var latest = completed[^1];
        var sign = direction == Direction.Buy ? 1m : -1m;
        var hasFiveMinuteBreak = false;
        DateTimeOffset? confirmationOpen = null;
        var five = MiddayChopConfirmationPolicy.AggregateFiveMinuteBars(completed, intervalSeconds).TakeLast(4).ToArray();
        if (five.Length == 4 &&
            five[^1].OpenTimeUtc.AddMinutes(5) <= asOf &&
            asOf - five[^1].OpenTimeUtc.AddMinutes(5) < TimeSpan.FromMinutes(5) &&
            !five.Zip(five.Skip(1)).Any(p => p.Second.OpenTimeUtc - p.First.OpenTimeUtc != TimeSpan.FromMinutes(5)))
        {
            var previousPivot = Extreme(five.Take(3), direction);
            hasFiveMinuteBreak = sign * (five[^1].Close - five[^1].Open) > 0m &&
                sign * (five[^1].Close - previousPivot) > 0m &&
                sign * (latest.Close - previousPivot) > 0m;
            confirmationOpen = five[^1].OpenTimeUtc;
        }
        if (recentOppositeFailure && (!hasFiveMinuteBreak || confirmationOpen < oppositeFailedExit))
            return WaitForFailedFlip();
        if (sameSideFailure && (!hasFiveMinuteBreak || confirmationOpen < sameSideFailedExit) &&
            !HasFreshCompactBase(completed, direction, atr, interval, asOf, sameSideFailedExit!.Value))
            return WaitForFailedRetry();

        // A large move can be the sum of ordinary candles, so the existing
        // single-bar impulse cap and distance from the session open can miss it.
        // Only defer a stretched entry still at the extreme of that local leg.
        var fiveMinutes = 300 / intervalSeconds;
        var impulse = completed.TakeLast(2 * fiveMinutes + 1).ToArray();
        if (atr > 0m && impulse.Length == 2 * fiveMinutes + 1 &&
            latest.OpenTimeUtc.Add(interval) > asOf - interval &&
            !impulse.Zip(impulse.Skip(1)).Any(pair => pair.Second.OpenTimeUtc - pair.First.OpenTimeUtc != interval))
        {
            var move = sign * (latest.Close - impulse[fiveMinutes].Close) / atr;
            var priorMove = sign * (impulse[fiveMinutes].Close - impulse[0].Close) / atr;
            var roomFromExtreme = sign * (Extreme(impulse.Skip(fiveMinutes + 1), direction) - latest.Close) / atr;
            if (move >= ExtendedImpulseAtr && move >= priorMove * MinimumImpulseAcceleration &&
                roomFromExtreme <= SessionMoveQualityPolicy.NearExtremeAtr)
                return new(false, ExtendedImpulse, [string.Create(CultureInfo.InvariantCulture,
                    $"The last five minutes already moved {move:F2} ATR and price remains near that extreme. Wait for the impulse to cool before taking a new breakout.")]);
        }

        // Preserve openings and ordinary continuations. The additional
        // location checks need a contiguous prior hour, not just one swing.
        var hour = 3600 / intervalSeconds;
        var context = new List<StrategyPriceBar>(5400 / intervalSeconds + 1) { latest };
        for (var i = completed.Length - 2; i >= 0 && context.Count <= 5400 / intervalSeconds; i--)
        {
            if (context[^1].OpenTimeUtc - completed[i].OpenTimeUtc != interval) break;
            context.Add(completed[i]);
        }
        if (atr <= 0m || context.Count <= hour) return Allowed;

        // A short break going against the broader move needs room to the
        // preceding hour's support/resistance, or a close through it.
        var priorHourLevel = Extreme(context.Skip(1).Take(hour), direction);
        var room = sign * (priorHourLevel - latest.Close);
        if (sign * (latest.Close - context[^1].Close) <= -atr && room >= 0m && room <= .75m * atr)
            return new(false, OpposingLevel, [string.Create(CultureInfo.InvariantCulture,
                $"Countertrend breakout has only {room / atr:F2} ATR of room to prior-hour {(direction == Direction.Buy ? "resistance" : "support")} at {priorHourLevel:F2}. Wait for a close through that level.")]);

        // A tiny one-bar break against the hour's direction can be noise even
        // if the earlier failed trade never opened. Require five-minute proof.
        var localLevel = Extreme(context.Skip(1).Take(5), direction);
        var clearance = sign * (latest.Close - localLevel);
        if (sign * (latest.Close - context[hour].Close) <= -atr && clearance < .10m * atr &&
            !hasFiveMinuteBreak)
            return new(false, MarginalBreak, [string.Create(CultureInfo.InvariantCulture,
                $"Breakout cleared its local pivot by only {clearance / atr:F2} ATR against the prior hour. Wait for a completed five-minute break.")]);
        return Allowed;
    }

    private static decimal Extreme(IEnumerable<StrategyPriceBar> bars, Direction direction) =>
        direction == Direction.Buy ? bars.Max(b => b.High) : bars.Min(b => b.Low);

    private static bool HasFreshCompactBase(StrategyPriceBar[] completed,
        Direction direction, decimal atr, TimeSpan interval, DateTimeOffset asOf, DateTimeOffset failedAt)
    {
        if (atr <= 0m || completed.Length < 5) return false;
        var pattern = completed.TakeLast(5).ToArray();
        var trigger = pattern[^1];
        if (pattern[0].OpenTimeUtc < failedAt || trigger.OpenTimeUtc.Add(interval) <= asOf - interval ||
            pattern.Zip(pattern.Skip(1)).Any(pair => pair.Second.OpenTimeUtc - pair.First.OpenTimeUtc != interval))
            return false;
        var pause = pattern.SkipLast(1).ToArray();
        var sign = direction == Direction.Buy ? 1m : -1m;
        return pause.Max(b => b.High) - pause.Min(b => b.Low) <= CompactRetryBaseAtr * atr &&
            pause.Any(b => sign * (b.Close - b.Open) < 0m) &&
            sign * (trigger.Close - trigger.Open) > 0m &&
            sign * (trigger.Close - Extreme(pause, direction)) > 0m;
    }

    private static BankNiftyEntryStructureDecision WaitForFailedFlip() =>
        new(false, FailedFlip, ["After an opposite-side failed setup, wait for a fresh completed five-minute break of the prior three-bar range."]);

    private static BankNiftyEntryStructureDecision WaitForFailedRetry() =>
        new(false, FailedRetry, ["After a losing same-side failed setup, wait for a fresh five-minute range break or a confirmed break of a compact post-exit base. Changing strikes or waiting out the cooldown does not confirm a new setup."]);
}
