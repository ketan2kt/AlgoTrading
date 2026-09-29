using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Execution;

public sealed record StructuralTransitionResearchDecision(Direction? Direction,
    decimal Confidence, decimal StructuralStop, IReadOnlyList<string> Reasons);

/// <summary>
/// Detects the first completed retest after a genuine break of recent structure.  This is a
/// paper-research challenger: it deliberately looks before the established portfolio strategies,
/// while still requiring displacement, acceptance beyond structure and a controlled retest.
/// </summary>
public static class StructuralTransitionResearchPolicy
{
    public const string StrategyCode = "Research|nifty-structure-retest";
    public const string Version = "1.0.0";
    public const int MaximumEntriesPerDay = 2;

    public static StructuralTransitionResearchDecision Evaluate(
        IReadOnlyList<StrategyPriceBar> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Count < 18)
            return Reject("At least 18 completed candles are required for structure-retest research.");

        var bars = source.TakeLast(Math.Min(30, source.Count)).ToArray();
        var atr = AverageTrueRange(bars.TakeLast(15).ToArray());
        if (atr <= 0) return Reject("ATR is unavailable.");

        // Freeze structure before the two possible break candles and the completed retest candle.
        var structure = bars.SkipLast(3).TakeLast(12).ToArray();
        var breakCandidates = bars.SkipLast(1).TakeLast(2).ToArray();
        var latest = bars[^1];
        var structureHigh = structure.Max(value => value.High);
        var structureLow = structure.Min(value => value.Low);

        var bullishBreak = breakCandidates.LastOrDefault(value =>
            value.Close >= structureHigh + atr * .08m &&
            value.Close - value.Open >= atr * .45m);
        if (bullishBreak is not null)
        {
            var retestDepth = (structureHigh - latest.Low) / atr;
            var accepted = latest.Close >= structureHigh - atr * .05m;
            var rejectedHigher = latest.Close > latest.Open && latest.Close > breakCandidates[^1].Low;
            var notExtended = latest.Close <= structureHigh + atr * .55m;
            if (retestDepth is >= -.30m and <= .35m && accepted && rejectedHigher && notExtended)
                return Accept(Direction.Buy,
                    Math.Min(latest.Low, structureHigh - atr * .12m), structureHigh, atr);
        }

        var bearishBreak = breakCandidates.LastOrDefault(value =>
            value.Close <= structureLow - atr * .08m &&
            value.Open - value.Close >= atr * .45m);
        if (bearishBreak is not null)
        {
            var retestDepth = (latest.High - structureLow) / atr;
            var accepted = latest.Close <= structureLow + atr * .05m;
            var rejectedLower = latest.Close < latest.Open && latest.Close < breakCandidates[^1].High;
            var notExtended = latest.Close >= structureLow - atr * .55m;
            if (retestDepth is >= -.30m and <= .35m && accepted && rejectedLower && notExtended)
                return Accept(Direction.Sell,
                    Math.Max(latest.High, structureLow + atr * .12m), structureLow, atr);
        }

        return Reject("No displaced break, acceptance and first controlled retest are present together.");
    }

    public static bool CanOpen(int entriesToday, bool hasActivePosition) =>
        entriesToday < MaximumEntriesPerDay && !hasActivePosition;

    private static StructuralTransitionResearchDecision Accept(Direction direction,
        decimal stop, decimal level, decimal atr) => new(direction, .72m, stop,
        [
            $"A displaced candle closed beyond {level:F2} structure.",
            "Price accepted the break and completed its first controlled retest.",
            $"The rejection completed within the early-entry budget; ATR is {atr:F2}.",
            "This challenger is paper-only until chronological validation proves positive expectancy."
        ]);

    private static StructuralTransitionResearchDecision Reject(string reason) =>
        new(null, 0m, 0m, [reason]);

    private static decimal AverageTrueRange(StrategyPriceBar[] bars)
    {
        var ranges = new List<decimal>();
        for (var index = 1; index < bars.Length; index++)
            ranges.Add(Math.Max(bars[index].High - bars[index].Low,
                Math.Max(Math.Abs(bars[index].High - bars[index - 1].Close),
                    Math.Abs(bars[index].Low - bars[index - 1].Close))));
        return ranges.Count == 0 ? 0m : ranges.Average();
    }
}
