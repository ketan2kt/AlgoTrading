using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Execution;

public enum DirectionalThesisPhaseV1
{
    Neutral,
    Emerging,
    Maintained,
    EntryWindow,
    Invalidated
}

public sealed record PersistentDirectionalThesisV1(
    string Version,
    string Status,
    DateOnly PaperEligibleFrom,
    Direction? Direction,
    decimal Confidence,
    DirectionalThesisPhaseV1 Phase,
    int BarsAlignedWithVwap,
    int FailedCounterTrendAttempts,
    bool OpeningRangeBreakHeld,
    bool ChangeOfCharacter,
    bool WouldEnter,
    IReadOnlyList<string> Evidence);

public sealed record PersistentDirectionalPaperEntryV1(
    Direction? Direction,
    decimal Confidence,
    decimal StructuralStop,
    decimal Target,
    IReadOnlyList<string> Reasons);

/// <summary>
/// Converts a completed persistent-thesis pullback window into a paper-only candidate.
/// The thesis already supplies multi-candle persistence and anti-chase checks; this policy
/// adds a bounded structural stop and never produces a live-eligible strategy source.
/// </summary>
public static class PersistentDirectionalPaperEntryPolicyV1
{
    public const string StrategyPrefix = "PaperOnly|persistent-directional-thesis-v1|";

    public static string StrategyCode(string market) => StrategyPrefix + market;

    public static PersistentDirectionalPaperEntryV1 Evaluate(
        IReadOnlyList<StrategyPriceBar> source, PersistentDirectionalThesisV1 thesis)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(thesis);
        if (thesis.Status != PersistentDirectionalThesisAnalyzerV1.Status ||
            !thesis.WouldEnter || thesis.Direction is null ||
            thesis.Phase != DirectionalThesisPhaseV1.EntryWindow)
            return Reject("The persistent directional thesis has no completed entry window.");
        if (source.Count < 15)
            return Reject("At least 15 completed candles are required.");

        var bars = source.TakeLast(15).ToArray();
        var recent = bars.TakeLast(5).ToArray();
        var latest = recent[^1];
        var atr = Atr(bars);
        if (atr <= 0m) return Reject("ATR is unavailable.");

        var stop = thesis.Direction == Direction.Buy
            ? Math.Min(recent.Min(value => value.Low), latest.Close - atr * .70m)
            : Math.Max(recent.Max(value => value.High), latest.Close + atr * .70m);
        var risk = Math.Abs(latest.Close - stop);
        if (risk < atr * .35m || risk > atr * 1.50m)
            return Reject($"Structural risk {risk / atr:F2} ATR is outside the 0.35-1.50 ATR paper-entry band.");

        var target = thesis.Direction == Direction.Buy
            ? latest.Close + risk * 1.80m
            : latest.Close - risk * 1.80m;
        return new(thesis.Direction, thesis.Confidence, stop, target,
            [.. thesis.Evidence,
             "Persistent thesis completed a failed-countertrend pullback entry window.",
             "Paper-only source: live execution is explicitly prohibited."]);
    }

    private static PersistentDirectionalPaperEntryV1 Reject(string reason) =>
        new(null, 0m, 0m, 0m, [reason]);

    private static decimal Atr(StrategyPriceBar[] bars) => bars.Skip(1)
        .Select((bar, index) => Math.Max(bar.High - bar.Low,
            Math.Max(Math.Abs(bar.High - bars[index].Close), Math.Abs(bar.Low - bars[index].Close))))
        .DefaultIfEmpty(0m).Average();
}

/// <summary>
/// Persistent multi-candle thesis used by the explicitly paper-only entry policy.
/// </summary>
public static class PersistentDirectionalThesisAnalyzerV1
{
    public const string Version = "persistent-directional-thesis-v1";
    public const string AuditOutcome = "DirectionalThesisChallenger:v1";
    public const string Status = "PaperEligible";
    public static readonly DateOnly PaperEligibleFrom = new(2026, 10, 16);

    public static PersistentDirectionalThesisV1 Analyze(
        IReadOnlyList<StrategyPriceBar> source,
        decimal vwap,
        decimal openingRangeHigh,
        decimal openingRangeLow,
        PersistentDirectionalThesisV1? previous = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Count < 15 || vwap <= 0m || openingRangeHigh < openingRangeLow)
            return Result(null, 0m, DirectionalThesisPhaseV1.Neutral, 0, 0,
                false, false, false,
                ["At least 15 completed candles, VWAP and a valid opening range are required."]);

        var bars = source.TakeLast(Math.Min(30, source.Count)).ToArray();
        var recent = bars.TakeLast(Math.Min(10, bars.Length)).ToArray();
        var atr = Atr(bars.TakeLast(15).ToArray());
        if (atr <= 0m)
            return Result(null, 0m, DirectionalThesisPhaseV1.Neutral, 0, 0,
                false, false, false, ["ATR is unavailable."]);

        var closes = bars.Select(value => value.Close).ToArray();
        var fast = Ema(closes, 9);
        var slow = Ema(closes, Math.Min(21, closes.Length));
        var bearishVwapBars = recent.Count(value => value.Close < vwap);
        var bullishVwapBars = recent.Count(value => value.Close > vwap);
        var bearishPullbacks = FailedCounterTrendAttempts(recent, Direction.Sell);
        var bullishPullbacks = FailedCounterTrendAttempts(recent, Direction.Buy);
        var bearishStructure = StructureScore(recent, Direction.Sell);
        var bullishStructure = StructureScore(recent, Direction.Buy);
        var bearishOpeningBreak = recent.TakeLast(3).Count(value => value.Close < openingRangeLow) >= 2;
        var bullishOpeningBreak = recent.TakeLast(3).Count(value => value.Close > openingRangeHigh) >= 2;

        var bearishScore = Score(Direction.Sell, recent, vwap, fast, slow,
            bearishVwapBars, bearishPullbacks, bearishStructure, bearishOpeningBreak);
        var bullishScore = Score(Direction.Buy, recent, vwap, fast, slow,
            bullishVwapBars, bullishPullbacks, bullishStructure, bullishOpeningBreak);

        var freshDirection = bearishScore >= .58m && bearishScore - bullishScore >= .12m
            ? Direction.Sell
            : bullishScore >= .58m && bullishScore - bearishScore >= .12m
                ? Direction.Buy
                : (Direction?)null;
        var previousDirection = previous is { Status: Status } ? previous.Direction : null;
        var changeOfCharacter = previousDirection is not null &&
            HasChangeOfCharacter(recent, previousDirection.Value, vwap);
        var direction = freshDirection;
        var phase = direction is null ? DirectionalThesisPhaseV1.Neutral
            : DirectionalThesisPhaseV1.Emerging;

        // Hysteresis: ordinary countertrend candles do not erase a well-established thesis.
        if (previousDirection is not null && !changeOfCharacter)
        {
            var retainedScore = previousDirection == Direction.Sell ? bearishScore : bullishScore;
            if (freshDirection == previousDirection || retainedScore >= .48m)
            {
                direction = previousDirection;
                phase = DirectionalThesisPhaseV1.Maintained;
            }
        }
        else if (changeOfCharacter)
        {
            phase = DirectionalThesisPhaseV1.Invalidated;
            if (freshDirection == previousDirection) direction = null;
        }

        var confidence = direction == Direction.Sell ? bearishScore
            : direction == Direction.Buy ? bullishScore : Math.Max(bearishScore, bullishScore);
        var alignedBars = direction == Direction.Sell ? bearishVwapBars
            : direction == Direction.Buy ? bullishVwapBars : 0;
        var failedPullbacks = direction == Direction.Sell ? bearishPullbacks
            : direction == Direction.Buy ? bullishPullbacks : 0;
        var rangeBreak = direction == Direction.Sell ? bearishOpeningBreak
            : direction == Direction.Buy && bullishOpeningBreak;
        var latest = recent[^1];
        var extensionFromFast = Math.Abs(latest.Close - fast) / atr;
        var recentFailure = HasRecentFailedCounterTrendAttempt(recent, direction);
        var wouldEnter = direction is not null && !changeOfCharacter && recentFailure &&
                         alignedBars >= 7 && extensionFromFast <= 1.85m;
        if (wouldEnter) phase = DirectionalThesisPhaseV1.EntryWindow;

        var evidence = new List<string>
        {
            $"{alignedBars} of the latest {recent.Length} closes align with the thesis side of VWAP.",
            $"{failedPullbacks} failed countertrend attempt(s) were observed.",
            $"Opening-range break held: {rangeBreak}.",
            $"EMA 9/21: {fast:F2}/{slow:F2}; extension from EMA 9 is {extensionFromFast:F2} ATR.",
            wouldEnter
                ? "A failed countertrend pullback completed without an extended entry."
                : "No shadow entry window is recorded until a failed countertrend pullback completes at acceptable extension."
        };
        if (previousDirection is not null)
            evidence.Add(changeOfCharacter
                ? "A two-step structural change of character invalidated the previous thesis."
                : "The prior directional thesis was retained through ordinary countertrend candles.");

        return Result(direction, confidence, phase, alignedBars, failedPullbacks,
            rangeBreak, changeOfCharacter, wouldEnter, evidence);
    }

    private static decimal Score(Direction direction, StrategyPriceBar[] bars,
        decimal vwap, decimal fast, decimal slow, int alignedBars, int failedPullbacks,
        decimal structure, bool openingBreak)
    {
        var latest = bars[^1];
        var vwapAlignment = (decimal)alignedBars / bars.Length;
        var emaAligned = direction == Direction.Sell ? fast < slow : fast > slow;
        var priceAligned = direction == Direction.Sell ? latest.Close < vwap : latest.Close > vwap;
        var sessionMove = direction == Direction.Sell
            ? Math.Max(0m, bars[0].Open - latest.Close)
            : Math.Max(0m, latest.Close - bars[0].Open);
        var totalRange = Math.Max(.01m, bars.Max(value => value.High) - bars.Min(value => value.Low));
        return Math.Clamp(
            vwapAlignment * .28m +
            (openingBreak ? .20m : 0m) +
            (emaAligned ? .14m : 0m) +
            structure * .16m +
            Math.Min(3, failedPullbacks) * .06m +
            (priceAligned ? .07m : 0m) +
            Math.Min(.13m, sessionMove / totalRange * .13m), 0m, .95m);
    }

    private static decimal StructureScore(StrategyPriceBar[] bars, Direction direction)
    {
        if (bars.Length < 4) return 0m;
        var comparisons = bars.Zip(bars.Skip(1)).ToArray();
        var aligned = direction == Direction.Sell
            ? comparisons.Count(value => value.Second.High <= value.First.High &&
                                         value.Second.Low <= value.First.Low &&
                                         (value.Second.High < value.First.High ||
                                          value.Second.Low < value.First.Low))
            : comparisons.Count(value => value.Second.Low >= value.First.Low &&
                                         value.Second.High >= value.First.High &&
                                         (value.Second.Low > value.First.Low ||
                                          value.Second.High > value.First.High));
        return (decimal)aligned / comparisons.Length;
    }

    private static int FailedCounterTrendAttempts(StrategyPriceBar[] bars, Direction thesis)
    {
        var count = 0;
        for (var index = 0; index < bars.Length - 1; index++)
        {
            var countertrend = thesis == Direction.Sell
                ? bars[index].Close > bars[index].Open
                : bars[index].Close < bars[index].Open;
            if (!countertrend) continue;
            var next = bars[index + 1];
            var rejected = thesis == Direction.Sell
                ? next.Close < bars[index].Open
                : next.Close > bars[index].Open;
            if (rejected) count++;
        }
        return count;
    }

    private static bool HasRecentFailedCounterTrendAttempt(StrategyPriceBar[] bars,
        Direction? direction)
    {
        if (direction is null || bars.Length < 3) return false;
        return FailedCounterTrendAttempts(bars.TakeLast(3).ToArray(), direction.Value) > 0;
    }

    private static bool HasChangeOfCharacter(StrategyPriceBar[] bars, Direction previous,
        decimal vwap)
    {
        if (bars.Length < 5) return false;
        var latest = bars[^1];
        var prior = bars[^2];
        var structure = bars.SkipLast(2).TakeLast(3).ToArray();
        return previous == Direction.Sell
            ? latest.Close > vwap && prior.Close > vwap &&
              latest.Close > structure.Max(value => value.High)
            : latest.Close < vwap && prior.Close < vwap &&
              latest.Close < structure.Min(value => value.Low);
    }

    private static decimal Atr(StrategyPriceBar[] bars)
    {
        if (bars.Length < 2) return 0m;
        return bars.Skip(1).Select((bar, index) => Math.Max(bar.High - bar.Low,
            Math.Max(Math.Abs(bar.High - bars[index].Close),
                Math.Abs(bar.Low - bars[index].Close)))).Average();
    }

    private static decimal Ema(decimal[] values, int period)
    {
        if (values.Length == 0) return 0m;
        var actual = Math.Min(period, values.Length);
        var multiplier = 2m / (actual + 1m);
        var ema = values.Take(actual).Average();
        foreach (var value in values.Skip(actual)) ema = (value - ema) * multiplier + ema;
        return ema;
    }

    private static PersistentDirectionalThesisV1 Result(Direction? direction,
        decimal confidence, DirectionalThesisPhaseV1 phase, int alignedBars,
        int failedPullbacks, bool rangeBreak, bool changeOfCharacter, bool wouldEnter,
        IReadOnlyList<string> evidence) => new(Version, Status, PaperEligibleFrom,
        direction, decimal.Round(Math.Clamp(confidence, 0m, .95m), 4), phase,
        alignedBars, failedPullbacks, rangeBreak, changeOfCharacter, wouldEnter, evidence);
}
