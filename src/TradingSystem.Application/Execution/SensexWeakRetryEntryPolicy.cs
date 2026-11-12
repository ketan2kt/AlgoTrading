using System.Globalization;
using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Execution;

public sealed record SensexWeakRetryDecision(bool Permitted, string? Rejection, IReadOnlyList<string> Reasons);

// A same-side retry cannot use a small local break as proof that the next
// opposing level has cleared. Rebuild the watch from completed cash candles,
// so restarts and the historical adapter cannot lose a pending retest.
public static class SensexWeakRetryEntryPolicy
{
    public const string Version = "sensex-weak-retry-retest-v1";
    public const string Deferred = "SensexWeakRetryRetestDeferred";
    private const decimal MinimumEfficiency = .30m;
    private const decimal NearbyRoomAtr = .50m;
    private const decimal MinimumBreakAtr = .10m;
    private const decimal RetestToleranceAtr = .25m;
    private static readonly SensexWeakRetryDecision Allowed = new(true, null, []);

    public static SensexWeakRetryDecision Evaluate(IReadOnlyList<StrategyPriceBar> source,
        Direction direction, DateTimeOffset sessionOpen, DateTimeOffset asOf, int intervalSeconds,
        DateTimeOffset? lastExit, string? lastStatus, decimal? lastPnl,
        MarketStateAssessmentV2? state)
    {
        if (lastExit is not { } exited || exited < sessionOpen || exited > asOf ||
            lastStatus != "FailedSetupExit" || lastPnl is not < 0m ||
            state?.DirectionalEfficiency >= MinimumEfficiency)
            return Allowed;
        if (direction is not (Direction.Buy or Direction.Sell))
            throw new ArgumentOutOfRangeException(nameof(direction));
        if (intervalSeconds is not (60 or 300)) return Incomplete();

        var interval = TimeSpan.FromSeconds(intervalSeconds);
        var bars = source.Where(b => b.OpenTimeUtc >= sessionOpen &&
                b.OpenTimeUtc.Add(interval) <= asOf).ToArray();
        if (bars.Length < 15 || asOf - bars[^1].OpenTimeUtc.Add(interval) >= interval)
            return Incomplete();

        var sign = direction == Direction.Buy ? 1m : -1m;
        Watch? watch = null;
        var contiguousStart = 0;
        for (var i = 1; i < bars.Length; i++)
        {
            var bar = bars[i];
            if (bar.OpenTimeUtc - bars[i - 1].OpenTimeUtc != interval)
            {
                contiguousStart = i;
                watch = null;
            }
            if (bar.OpenTimeUtc < exited || i - contiguousStart < 14) continue;

            var aligned = sign * (bar.Close - bar.Open) > 0m;
            if (watch is { } active)
            {
                var distance = sign * (bar.Close - active.Level);
                if (distance >= -active.Atr)
                {
                    var near = sign * ((direction == Direction.Buy ? bar.Low : bar.High) - active.Level);
                    var tolerance = RetestToleranceAtr * active.Atr;
                    if (distance < 0m || active.Broke && near < -tolerance)
                        watch = new(active.Level, active.Atr);
                    else if (!active.Broke && aligned && distance >= MinimumBreakAtr * active.Atr)
                        watch = active with { Broke = true };
                    else if (active.Broke && active.Pivot is { } pivot && aligned &&
                        sign * (bar.Close - pivot) > 0m &&
                        distance <= FirstEntryTimingPolicy.MaximumTriggerExtensionAtr * active.Atr)
                        watch = active with { Confirmed = true };
                    else if (active.Broke && near <= tolerance)
                        watch = active with { Pivot = direction == Direction.Buy ? bar.High : bar.Low, Confirmed = false };
                    continue;
                }
                // Price returned to the range instead of testing this level.
                watch = null;
            }

            var contextStart = Math.Max(contiguousStart, i - 29);
            var context = bars.AsSpan(contextStart, i - contextStart + 1);
            var efficiency = i == bars.Length - 1 && state is not null ? state.DirectionalEfficiency : Efficiency(context);
            if ((!aligned && i != bars.Length - 1) || efficiency >= MinimumEfficiency) continue;
            var atr = Atr(context[^15..]);
            if (atr <= 0m) continue;
            decimal? level = null;
            var room = NearbyRoomAtr * atr;
            foreach (var earlier in context[..^1])
            {
                var price = direction == Direction.Buy ? earlier.High : earlier.Low;
                var distance = sign * (price - bar.Close);
                if (distance < 0m || distance > room) continue;
                (room, level) = (distance, price);
            }
            if (level is { } nearby) watch = new(nearby, atr);
        }

        if (contiguousStart > 0 && bars.Length - contiguousStart < 30 && bars[contiguousStart].OpenTimeUtc > exited)
            return Incomplete();
        if (watch is not { } pending) return Allowed;
        var latest = bars[^1].Close;
        if (pending.Confirmed && pending.Pivot is { } confirmed &&
            sign * (latest - confirmed) > 0m && sign * (latest - pending.Level) <=
                FirstEntryTimingPolicy.MaximumTriggerExtensionAtr * pending.Atr)
            return Allowed;
        return new(false, Deferred, [string.Create(CultureInfo.InvariantCulture,
            $"Weak Sensex same-side retry: wait for a completed break, held retest and non-chasing resumption at {(direction == Direction.Buy ? "resistance" : "support")} {pending.Level:F2}.")]);
    }

    private static decimal Efficiency(ReadOnlySpan<StrategyPriceBar> bars)
    {
        decimal travelled = 0m;
        for (var i = 1; i < bars.Length; i++) travelled += Math.Abs(bars[i].Close - bars[i - 1].Close);
        return travelled <= 0m ? 0m : Math.Abs(bars[^1].Close - bars[0].Close) / travelled;
    }

    private static decimal Atr(ReadOnlySpan<StrategyPriceBar> bars)
    {
        decimal total = 0m;
        for (var i = 1; i < bars.Length; i++)
            total += Math.Max(bars[i].High - bars[i].Low,
                Math.Max(Math.Abs(bars[i].High - bars[i - 1].Close), Math.Abs(bars[i].Low - bars[i - 1].Close)));
        return total / (bars.Length - 1);
    }

    private static SensexWeakRetryDecision Incomplete() => new(false, Deferred,
        ["Weak Sensex same-side retry needs contiguous, current completed cash candles."]);

    private sealed record Watch(decimal Level, decimal Atr, bool Broke = false, decimal? Pivot = null, bool Confirmed = false);
}
