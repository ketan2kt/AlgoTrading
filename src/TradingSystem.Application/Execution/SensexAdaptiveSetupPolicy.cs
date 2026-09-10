using TradingSystem.Application.MarketData;
using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Execution;

public enum SensexMarketState { DirectionalTrend, StructuredRange, Transition, OpeningShock }
public enum SensexSetupVerdict { Prefer, Observe, Avoid }

public sealed record SensexAdaptiveSetupAssessment(string Version, bool ShadowOnly,
    SensexMarketState State, SensexSetupVerdict Verdict, string Setup,
    decimal TrendProbability, decimal RangeProbability, decimal TransitionProbability,
    decimal Efficiency, decimal FlipRatio, decimal EmaSeparationAtr,
    decimal MoveFromTriggerAtr, decimal? RoomToOpposingLevelAtr,
    IReadOnlyList<string> SupportingEvidence, IReadOnlyList<string> Concerns,
    IReadOnlyList<string> Limitations);

public static class SensexAdaptiveSetupPolicy
{
    private static SensexAdaptiveSetupAssessment Empty(string limitation) =>
        new("sensex-adaptive-v2", false, SensexMarketState.Transition,
            SensexSetupVerdict.Observe, "CollectEvidence", 0, 0, 1, 0, 0, 0, 0,
            null, [], [], [limitation]);

    public static SensexAdaptiveSetupAssessment Assess(IReadOnlyList<StrategyPriceBar> source,
        Direction direction, decimal atr, bool openingShock)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Count < 12 || atr <= 0) return Empty("Insufficient completed candles or ATR.");
        var bars = source.TakeLast(Math.Min(30, source.Count)).ToArray();
        var closes = bars.Select(x => x.Close).ToArray();
        var fast = TechnicalIndicators.ExponentialMovingAverage(closes, 9);
        var slow = TechnicalIndicators.ExponentialMovingAverage(closes, Math.Min(21, closes.Length));
        var changes = closes.Zip(closes.Skip(1), (a, b) => b - a).ToArray();
        var travel = changes.Sum(Math.Abs);
        var efficiency = travel == 0 ? 0 : Math.Abs(closes[^1] - closes[0]) / travel;
        var signs = changes.Where(x => x != 0).Select(Math.Sign).ToArray();
        var flips = signs.Zip(signs.Skip(1), (a, b) => a != b).Count(x => x);
        var flipRatio = signs.Length < 2 ? 0 : (decimal)flips / (signs.Length - 1);
        var separation = Math.Abs(fast - slow) / atr;
        var trend = Math.Clamp(efficiency * .7m + Math.Min(separation, 1m) * .3m, 0, 1);
        var range = Math.Clamp((1 - efficiency) * .55m + flipRatio * .45m, 0, 1);
        var transition = Math.Clamp(1 - Math.Abs(trend - range), 0, 1);
        var state = openingShock ? SensexMarketState.OpeningShock
            : trend >= .58m && trend > range + .10m ? SensexMarketState.DirectionalTrend
            : range >= .58m && range > trend + .10m ? SensexMarketState.StructuredRange
            : SensexMarketState.Transition;
        var prior = bars.SkipLast(1).TakeLast(8).ToArray();
        var trigger = direction == Direction.Buy ? prior.Max(x => x.High) : prior.Min(x => x.Low);
        var extension = (direction == Direction.Buy ? closes[^1] - trigger : trigger - closes[^1]) / atr;
        var opposing = direction == Direction.Buy
            ? bars.SkipLast(1).Where(x => x.High > closes[^1]).Select(x => (decimal?)x.High).Min()
            : bars.SkipLast(1).Where(x => x.Low < closes[^1]).Select(x => (decimal?)x.Low).Max();
        decimal? room = opposing is null ? null : Math.Abs(opposing.Value - closes[^1]) / atr;
        var aligned = direction == Direction.Buy ? fast > slow : fast < slow;
        var concerns = new List<string>();
        if (!aligned) concerns.Add("Direction is not aligned with EMA 9/21.");
        if (extension > 1m) concerns.Add("Entry is more than one ATR beyond the local trigger; chase risk.");
        if (room < .5m) concerns.Add("Less than 0.5 ATR remains to the nearest observed opposing level.");
        if (state == SensexMarketState.StructuredRange && extension > 0)
            concerns.Add("Momentum continuation is mismatched with the current range hypothesis.");
        if (state is SensexMarketState.Transition or SensexMarketState.OpeningShock)
            concerns.Add("Market state is unstable; acceptance or a boundary reaction is not confirmed.");
        var supports = new List<string>();
        if (aligned) supports.Add("Direction is aligned with EMA 9/21.");
        if (extension is >= 0 and <= 1m) supports.Add("Price cleared the local trigger without exceeding one ATR.");
        if (room >= .5m) supports.Add("At least 0.5 ATR remains to an observed opposing level.");
        var setup = state == SensexMarketState.DirectionalTrend ? "ContinuationOrRetest"
            : state == SensexMarketState.StructuredRange ? "BoundaryReactionOnly" : "WaitForAcceptance";
        var verdict = concerns.Count == 0 ? SensexSetupVerdict.Prefer
            : concerns.Count >= 2 || !aligned ? SensexSetupVerdict.Avoid : SensexSetupVerdict.Observe;
        return new("sensex-adaptive-v2", false, state, verdict, setup, trend, range, transition,
            efficiency, flipRatio, separation, extension, room, supports, concerns,
            ["Probabilities are normalized hypotheses, not calibrated win probabilities.",
             "Observed local extremes are not guaranteed support/resistance."]);
    }

    public static bool AllowsMomentumEntry(SensexAdaptiveSetupAssessment assessment) =>
        assessment.State == SensexMarketState.DirectionalTrend &&
        assessment.Verdict == SensexSetupVerdict.Prefer;

}
