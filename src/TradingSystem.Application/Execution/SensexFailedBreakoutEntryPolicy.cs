using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Execution;

// Entry-only confirmation for the permissive early-pullback and range-fade paths.
// A directional thesis does not turn a failed breakout into a completed pullback.
public static class SensexFailedBreakoutEntryPolicy
{
    // Deferring an unconfirmed continuation must not immediately convert the
    // same unresolved auction into a range fade in the opposite direction.
    // A genuine break beyond the failed candle remains eligible.
    public static string? OppositeRangeRejection(IReadOnlyList<StrategyPriceBar> source,
        Direction direction, MarketStateAssessmentV2 state)
    {
        var failedSide = direction == Direction.Buy ? Direction.Sell : Direction.Buy;
        foreach (var failed in UnresolvedBreakouts(source, failedSide, state))
        {
            var reverseBoundary = direction == Direction.Buy ? failed.Failure.High : failed.Failure.Low;
            if (direction == Direction.Buy ? source[^1].Close > reverseBoundary : source[^1].Close < reverseBoundary)
                continue;
            return "Sensex range fade deferred: price is still inside an unresolved failed-breakout auction. Wait for a close beyond the failed candle in the new direction, not an automatic opposite-side replacement.";
        }
        return null;
    }

    public static string? Rejection(IReadOnlyList<StrategyPriceBar> source,
        Direction direction, MarketStateAssessmentV2 state)
    {
        foreach (var failed in UnresolvedBreakouts(source, direction, state))
            return $"Sensex early pullback deferred: the range breakout at {failed.Impulse.OpenTimeUtc:O} failed through its candle boundary. Wait for a close beyond {failed.Level:F2} or a newly accepted post-failure pullback pivot; a bounce alone is not confirmation.";
        return null;
    }

    private static IEnumerable<(StrategyPriceBar Impulse, StrategyPriceBar Failure, decimal Level)> UnresolvedBreakouts(
        IReadOnlyList<StrategyPriceBar> source, Direction direction, MarketStateAssessmentV2 state)
    {
        var sign = direction == Direction.Buy ? 1m : -1m;
        // Reuse the router's inefficient-path threshold and range playbook's
        // edges. Directional moves and pullbacks away from the edge are unchanged.
        if (state.DirectionalEfficiency > .30m ||
            (direction == Direction.Buy ? state.RangePosition < .78m : state.RangePosition > .22m))
            yield break;
        var bars = source.TakeLast(38).ToArray();
        if (bars.Length < 10) yield break;
        var latest = bars[^1];
        for (var i = Math.Max(8, bars.Length - 30); i < bars.Length - 1; i++)
        {
            var impulse = bars[i];
            var failure = bars[i + 1];
            var prior = bars.Skip(i - 8).Take(8).ToArray();
            var trigger = Extreme(prior, direction);
            var boundary = direction == Direction.Buy ? impulse.Low : impulse.High;
            var failedLevel = direction == Direction.Buy ? impulse.High : impulse.Low;
            // Reuse momentum's minimum impulse size. A tiny body or gap-only
            // print is not a directional breakout that can meaningfully fail.
            if (!IsDirectionalImpulse(bars, i, sign) ||
                sign * (impulse.Close - trigger) <= 0m ||
                sign * (failure.Close - boundary) >= 0m) continue;
            var after = bars.Skip(i + 1).ToArray();
            var interval = failure.OpenTimeUtc - impulse.OpenTimeUtc;
            if (interval.TotalSeconds is not (60 or 300) ||
                after.Zip(after.Skip(1)).Any(p => p.Second.OpenTimeUtc - p.First.OpenTimeUtc != interval))
                continue;
            if (after.Any(b => sign * (b.Close - failedLevel) > 0m)) continue;

            // Same two-close/three-bar-pivot acceptance used by the stalled
            // thesis check, but wholly AFTER the failed breakout. A genuinely
            // rebuilt pullback can resume below the old high (above the old low).
            var rebuilt = false;
            for (var j = i + 5; j < bars.Length; j++)
            {
                var pivot = Extreme(bars.Skip(j - 4).Take(3), direction);
                if (sign * (bars[j - 1].Close - pivot) > 0m &&
                    sign * (bars[j].Close - pivot) > 0m &&
                    sign * (latest.Close - pivot) > 0m)
                { rebuilt = true; break; }
            }
            if (!rebuilt)
                yield return (impulse, failure, failedLevel);
        }
    }

    private static decimal Extreme(IEnumerable<StrategyPriceBar> bars, Direction side) =>
        side == Direction.Buy ? bars.Max(b => b.High) : bars.Min(b => b.Low);

    private static bool IsDirectionalImpulse(StrategyPriceBar[] bars, int index, decimal sign)
    {
        var recent = bars.Take(index + 1).TakeLast(15).ToArray();
        if (recent.Length < 2) return false;
        var atr = recent.Skip(1).Select((b, i) => Math.Max(b.High - b.Low,
            Math.Max(Math.Abs(b.High - recent[i].Close), Math.Abs(b.Low - recent[i].Close)))).Average();
        return atr > 0m && sign * (bars[index].Close - bars[index].Open) >=
            IndexMomentumBreakoutPolicy.MinimumImpulseAtr * atr;
    }
}
