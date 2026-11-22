using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Execution;

public enum SharedMarketRegime { WarmingUp, Directional, BalancedRange, NoisyChop, Transition }
public enum MarketVolatilityState { Unavailable, Compressed, Normal, Expanded }
public enum EntryStrategyFamily { Continuation, Breakout, RangeRejection }

public sealed record SharedMarketContext(string Version, DateTimeOffset? AsOf,
    SharedMarketRegime Regime, Direction? Direction, MarketVolatilityState Volatility,
    decimal Efficiency, decimal DirectionFlipRate, decimal? StructuralBoundary,
    DateTimeOffset? StructureAsOf, IReadOnlyList<string> Evidence);

public sealed record MarketContextAdmission(bool Permitted, int Priority, IReadOnlyList<string> Reasons);

/// <summary>
/// Entry selection only. Reconstructs structural memory from completed session bars,
/// so restart and replay produce the same answer without mutable cross-market state.
/// Never changes an existing position, its stops, targets, size or profit protection.
/// </summary>
public static class SharedMarketContextPolicy
{
    public const string Version = "shared-market-context-v1";

    public static SharedMarketContext Analyze(IReadOnlyList<StrategyPriceBar> source,
        int intervalSeconds = 60)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Count == 0 || intervalSeconds is not (60 or 300))
            return Empty(null);
        // Inputs must already be closed. Reset at session boundaries or a missing
        // interval; never interpret an absent observation as a flat/choppy candle.
        var bars = CompletedTail(source, intervalSeconds);
        var asOf = bars[^1].OpenTimeUtc.AddSeconds(intervalSeconds);
        if (bars.Length < 12 || bars.Any(b => b.Close <= 0m || b.High < b.Low)) return Empty(asOf);

        var recent = bars.TakeLast(20).ToArray();
        var changes = recent.Zip(recent.Skip(1), (a,b) => b.Close-a.Close).ToArray();
        var distance = changes.Sum(Math.Abs);
        var efficiency = distance == 0m ? 0m : Math.Abs(recent[^1].Close-recent[0].Close)/distance;
        var signs = changes.Where(c => c != 0m).Select(Math.Sign).ToArray();
        var flips = signs.Length < 2 ? 0m : (decimal)signs.Zip(signs.Skip(1)).Count(p => p.First != p.Second)/(signs.Length-1);
        var volatilityRatio = Atr(bars.TakeLast(7).ToArray()) / Math.Max(.000001m, Atr(bars.TakeLast(31).ToArray()));
        var volatility = volatilityRatio < .68m ? MarketVolatilityState.Compressed :
            volatilityRatio > 1.45m ? MarketVolatilityState.Expanded : MarketVolatilityState.Normal;

        var five = MiddayChopConfirmationPolicy.AggregateFiveMinuteBars(bars, intervalSeconds);
        Direction? direction = null;
        decimal? boundary = null;
        Direction? invalidatedDirection = null;
        decimal? invalidatedBoundary = null;
        // Structural hysteresis: ordinary opposite-colour candles retain a thesis.
        // Two completed closes beyond its protected swing invalidate it. A new
        // direction must establish its own higher-high/higher-low (or inverse) structure.
        for (var count = 6; count <= five.Length; count++)
        {
            var window = five.Take(count).TakeLast(12).ToArray();
            var structure = MarketStructureAnalyzer.Analyze(window);
            var fresh = structure.Strength < .55m ? null : structure.Direction switch
            {
                MarketStructureDirection.Bullish => (Direction?)Domain.Trading.Direction.Buy,
                MarketStructureDirection.Bearish => Domain.Trading.Direction.Sell,
                _ => null
            };
            var invalidated = direction is not null && boundary is not null &&
                (direction == Domain.Trading.Direction.Buy
                    ? window.TakeLast(2).All(b => b.Close < boundary)
                    : window.TakeLast(2).All(b => b.Close > boundary));
            if (invalidated)
            {
                // A lagging window still pointing the old way cannot immediately
                // re-establish the thesis that these two closes invalidated.
                if (fresh == direction) fresh = null;
                invalidatedDirection = direction;
                invalidatedBoundary = boundary;
                direction = null; boundary = null;
            }
            if (fresh == invalidatedDirection && invalidatedBoundary is { } lostLevel &&
                !window.TakeLast(2).All(b => fresh == Domain.Trading.Direction.Buy ? b.Close > lostLevel : b.Close < lostLevel))
                fresh = null;
            if (direction is null) direction = fresh;
            if (direction is not null && fresh == direction)
            {
                var swing = direction == Domain.Trading.Direction.Buy ? structure.RecentSwingLow : structure.RecentSwingHigh;
                boundary = boundary is null ? swing : direction == Domain.Trading.Direction.Buy
                    ? Math.Max(boundary.Value, swing) : Math.Min(boundary.Value, swing);
            }
        }
        var regime = direction is not null ? SharedMarketRegime.Directional :
            efficiency < .38m && flips >= .40m ? SharedMarketRegime.NoisyChop :
            efficiency <= .30m ? SharedMarketRegime.BalancedRange : SharedMarketRegime.Transition;
        return new(Version, asOf, regime, direction, volatility, efficiency, flips,
            boundary, five.LastOrDefault()?.OpenTimeUtc.AddMinutes(5),
            [$"{Version}: {regime}, direction {direction?.ToString() ?? "unestablished"}, volatility {volatility}.",
             $"Completed structure boundary {boundary}; efficiency {efficiency:P0}, flips {flips:P0}. Entry selection only; existing risk gates remain mandatory."]);
    }

    public static MarketContextAdmission Select(SharedMarketContext context,
        IReadOnlyList<StrategyPriceBar> bars, Direction direction, EntryStrategyFamily family,
        int intervalSeconds = 60)
    {
        bars = CompletedTail(bars, intervalSeconds);
        if (bars.Count > 0 && context.AsOf != bars[^1].OpenTimeUtc.AddSeconds(intervalSeconds))
            return new(false, 0, ["Shared market context does not match this decision candle."]);
        if (context.Regime == SharedMarketRegime.WarmingUp)
            return new(true, 0, []); // Existing warmup/data gates still decide.
        // Candidates already passed their own pattern confirmation. A slower
        // descriptive thesis must not veto an accepted retest/pullback. Existing
        // countertrend, location, execution and risk gates remain mandatory.
        if (family == EntryStrategyFamily.Continuation)
            return new(true, context.Direction is null || context.Direction == direction ? 2 : 0, context.Evidence);
        if (context.Direction == direction)
            return new(true, family == EntryStrategyFamily.Continuation ? 2 : 1, context.Evidence);
        if (context.Direction is not null)
        {
            var confirmed = MiddayChopConfirmationPolicy.ConfirmCompletedStructure(bars, direction, intervalSeconds);
            var accepted = AcceptedBreak(bars, direction);
            return new(confirmed.Permitted || accepted, 0,
                [.. context.Evidence, confirmed.Permitted || accepted
                    ? "Opposing entry has an accepted range break or completed five-minute structure confirmation."
                    : "Retain the established direction; opposing entry lacks accepted structural reversal."]);
        }
        if (context.Regime is SharedMarketRegime.NoisyChop or SharedMarketRegime.BalancedRange)
        {
            var range = RangePlaybookV2.Evaluate(bars, new(IntradayMarketStateV2.StructuredRange,
                null, .5m, context.Efficiency, context.DirectionFlipRate, 0m, .5m, 0m, 0m, []));
            var permitted = family == EntryStrategyFamily.RangeRejection
                ? range.Direction == direction : AcceptedBreak(bars, direction) ||
                    MiddayChopConfirmationPolicy.ConfirmCompletedStructure(bars, direction, intervalSeconds).Permitted;
            return new(permitted, family == EntryStrategyFamily.RangeRejection ? 2 : 1,
                [.. context.Evidence, permitted ? "Existing range-edge rejection or accepted range escape is eligible."
                    : "Rotating structure: wait for a confirmed range-edge rejection or accepted escape, not a middle-range impulse."]);
        }
        return new(true, 0, context.Evidence);
    }

    public static EntryStrategyFamily Family(string strategy) =>
        strategy.Contains("range-edge", StringComparison.OrdinalIgnoreCase) ||
        strategy.Contains("rejection-reversal", StringComparison.OrdinalIgnoreCase)
            ? EntryStrategyFamily.RangeRejection :
        strategy.Contains("pullback", StringComparison.OrdinalIgnoreCase) ||
        strategy.Contains("retest", StringComparison.OrdinalIgnoreCase) ||
        strategy.Contains("participation", StringComparison.OrdinalIgnoreCase) ||
        strategy.Contains("thesis", StringComparison.OrdinalIgnoreCase)
            ? EntryStrategyFamily.Continuation : EntryStrategyFamily.Breakout;

    // A completed five-minute breakout can be followed by a valid retest before
    // the slower directional memory changes. This confirms that specific pattern;
    // it is not permission for every impulse against the established direction.
    public static bool HasCompletedBreakoutRetest(IReadOnlyList<StrategyPriceBar> source,
        Direction direction, int intervalSeconds = 60)
    {
        if (intervalSeconds != 60) return false;
        var bars = CompletedTail(source, intervalSeconds);
        var five = MiddayChopConfirmationPolicy.AggregateFiveMinuteBars(bars, intervalSeconds);
        if (bars.Length < 20 || five.Length < 4) return false;
        var prior = five.TakeLast(4).Take(2).ToArray();
        var impulse = five[^2];
        var retest = five[^1];
        var sign = direction == Domain.Trading.Direction.Buy ? 1m : -1m;
        var level = direction == Domain.Trading.Direction.Buy ? prior.Max(b=>b.High) : prior.Min(b=>b.Low);
        var edge = direction == Domain.Trading.Direction.Buy ? retest.Low : retest.High;
        var atr = Atr(bars.TakeLast(15).ToArray());
        if (atr <= 0m || sign*(impulse.Close-level) <= 0m || sign*(impulse.Close-impulse.Open) <= 0m ||
            sign*(retest.Close-retest.Open) >= 0m || sign*(retest.Close-level) < 0m ||
            // Reuse the completed-retest strategy's quarter-ATR touch/wick budget.
            sign*(edge-level) < -.25m*atr || sign*(edge-level) > .25m*atr) return false;
        var previous = bars[^2];
        var latest = bars[^1];
        return previous.OpenTimeUtc >= retest.OpenTimeUtc.AddMinutes(5) &&
            sign*(previous.Close-previous.Open) > 0m && sign*(latest.Close-latest.Open) > 0m &&
            sign*(latest.Close-(direction == Domain.Trading.Direction.Buy ? previous.High : previous.Low)) > 0m;
    }

    internal static bool AcceptedBreak(IReadOnlyList<StrategyPriceBar> source, Direction direction)
    {
        if (source.Count < 12) return false;
        var bars = source.TakeLast(22).ToArray();
        var prior = bars.SkipLast(2).ToArray();
        var previous = bars[^2]; var latest = bars[^1];
        var level = direction == Domain.Trading.Direction.Buy ? prior.Max(b => b.High) : prior.Min(b => b.Low);
        return direction == Domain.Trading.Direction.Buy
            ? previous.Close > level && latest.Close > level && latest.Close > latest.Open
            : previous.Close < level && latest.Close < level && latest.Close < latest.Open;
    }

    private static decimal Atr(StrategyPriceBar[] bars) => bars.Length < 2 ? 0m :
        bars.Skip(1).Select((b,i) => Math.Max(b.High-b.Low, Math.Max(Math.Abs(b.High-bars[i].Close),Math.Abs(b.Low-bars[i].Close)))).Average();
    private static StrategyPriceBar[] CompletedTail(IReadOnlyList<StrategyPriceBar> source, int intervalSeconds)
    {
        var ordered = source.OrderBy(b => b.OpenTimeUtc).ToArray();
        if (ordered.Length == 0) return ordered;
        var day = DateOnly.FromDateTime(ordered[^1].OpenTimeUtc.ToOffset(TimeSpan.FromHours(5.5)).DateTime);
        var start = ordered.Length - 1;
        while (start > 0 &&
               DateOnly.FromDateTime(ordered[start - 1].OpenTimeUtc.ToOffset(TimeSpan.FromHours(5.5)).DateTime) == day &&
               ordered[start].OpenTimeUtc - ordered[start - 1].OpenTimeUtc == TimeSpan.FromSeconds(intervalSeconds)) start--;
        return ordered[start..];
    }
    private static SharedMarketContext Empty(DateTimeOffset? at) => new(Version, at,
        SharedMarketRegime.WarmingUp, null, MarketVolatilityState.Unavailable, 0m, 0m, null, null,
        ["Shared market context is warming up; existing strategy/data/risk gates remain in force."]);
}
