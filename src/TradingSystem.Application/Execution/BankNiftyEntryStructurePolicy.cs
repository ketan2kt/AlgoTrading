using System.Globalization;
using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Execution;

public sealed record BankNiftyEntryStructureDecision(bool Permitted, string? Rejection,
    IReadOnlyList<string> Reasons);

public static class BankNiftyEntryStructurePolicy
{
    public const string Version = "bank-entry-structure-v1";
    public const string OpposingLevel = "BankNiftyCountertrendLevel";
    public const string MarginalBreak = "BankNiftyMarginalBreak";
    public const string FailedFlip = "BankNiftyFailedFlipUnconfirmed";
    private static readonly BankNiftyEntryStructureDecision Allowed = new(true, null, []);

    public static BankNiftyEntryStructureDecision Evaluate(IReadOnlyList<StrategyPriceBar> bars,
        Direction direction, decimal atr, int intervalSeconds, DateTimeOffset asOf,
        DateTimeOffset? oppositeFailedExit = null)
    {
        ArgumentNullException.ThrowIfNull(bars);
        if (direction is not (Direction.Buy or Direction.Sell))
            throw new ArgumentOutOfRangeException(nameof(direction));
        var recentOppositeFailure = BankNiftyFailedSetupCooldownPolicy.Blocks(asOf, oppositeFailedExit);
        if (intervalSeconds is not (60 or 300) || bars.Count == 0)
            return recentOppositeFailure ? WaitForFailedFlip() : Allowed;

        var interval = TimeSpan.FromSeconds(intervalSeconds);
        var completed = bars.Where(b => b.OpenTimeUtc.Add(interval) <= asOf).ToArray();
        if (completed.Length == 0) return recentOppositeFailure ? WaitForFailedFlip() : Allowed;
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

    private static BankNiftyEntryStructureDecision WaitForFailedFlip() =>
        new(false, FailedFlip, ["After an opposite-side failed setup, wait for a fresh completed five-minute break of the prior three-bar range."]);
}
