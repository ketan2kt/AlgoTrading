using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Execution;

public sealed record SensexReason(string Factor, string Status, string Explanation);
public sealed record SensexReasoningSnapshot(string Version, bool AdvisoryOnly,
    string Verdict, IReadOnlyList<SensexReason> Reasons, decimal Efficiency,
    decimal? ExtensionAtr, decimal? RoomAtr, decimal? Spread, decimal EstimatedCharges,
    decimal PlannedNetReward, decimal InitialRisk, decimal Stop, decimal Target,
    int SameDirectionPositions, int RecentLosingPositions,
    IReadOnlyList<StrategyPriceBar> ObservedBars);

public static class SensexTradeReasoning
{
    public static SensexReasoningSnapshot Assess(IReadOnlyList<StrategyPriceBar> bars,
        Direction direction, decimal atr, decimal entry, decimal stop, decimal target,
        int quantity, decimal? bid, decimal? ask, decimal? quoteAgeSeconds,
        decimal charges, int sameDirectionPositions, int recentLosses)
    {
        var reasons = new List<SensexReason>();
        var last = bars[^1];
        var prior = bars.SkipLast(1).TakeLast(5).ToArray();
        var travel = bars.Zip(bars.Skip(1), (a, b) => Math.Abs(b.Close - a.Close)).Sum();
        var efficiency = travel == 0 ? 0 : Math.Abs(last.Close - bars[0].Close) / travel;
        reasons.Add(new("Context", efficiency >= .32m ? "Supports" : "Concern",
            efficiency >= .32m ? "Recent price path is directional by efficiency hypothesis."
                : "Recent path is inefficient; momentum may be unsuitable in chop."));
        decimal? trigger = prior.Length == 0 ? null : direction == Direction.Buy
            ? prior.Max(x => x.High) : prior.Min(x => x.Low);
        decimal? extension = atr <= 0 || trigger is null ? null :
            (direction == Direction.Buy ? last.Close - trigger : trigger - last.Close) / atr;
        reasons.Add(new("Trigger", extension > 0 ? "Supports" : "Unknown",
            "Compare close with preceding five-bar extreme; this is not proof of a successful retest."));
        reasons.Add(new("Timing", extension is null ? "Unknown" : extension > 1 ? "Concern" : "Supports",
            "Extension above one ATR is a chase hypothesis; true setup age is not yet available."));
        var opposing = direction == Direction.Buy
            ? bars.SkipLast(1).Where(x => x.High > last.Close).Select(x => (decimal?)x.High).Min()
            : bars.SkipLast(1).Where(x => x.Low < last.Close).Select(x => (decimal?)x.Low).Max();
        decimal? room = atr <= 0 || opposing is null ? null : Math.Abs(opposing.Value - last.Close) / atr;
        reasons.Add(new("Location", room is null ? "Unknown" : room < .5m ? "Concern" : "Supports",
            "Room uses local observed extremes only; absent levels do not mean unlimited upside."));
        decimal? spread = bid > 0 && ask >= bid ? ask - bid : null;
        reasons.Add(new("Quote", quoteAgeSeconds is null || spread is null ? "Unknown"
            : quoteAgeSeconds < 0 || quoteAgeSeconds > 15 ? "Concern" : "Supports",
            "Quote age is last-trade age, not order latency; missing or crossed bid/ask is unknown."));
        var reward = (target - entry) * quantity - charges;
        reasons.Add(new("Economics", reward <= 0 || spread * quantity >= reward ? "Concern" : "Supports",
            "Planned target reward after estimated round-trip charges; not a forecast, excludes unknown slippage."));
        reasons.Add(new("Invalidation", stop > 0 && stop < entry && target > entry ? "Supports" : "Concern",
            "Long option premium stop invalidates the position; no claim it is an underlying structural level."));
        reasons.Add(new("Exposure", sameDirectionPositions > 0 ? "Concern" : "Supports",
            "Counts active same-direction CE/PE exposure; different strikes may repeat the same bet."));
        reasons.Add(new("Recent failures", recentLosses > 0 ? "Concern" : "Supports",
            "Counts same-direction losing positions closed in 60 minutes; does not prove the setup is identical."));
        return new("sensex-reasoning-v1", true,
            reasons.Any(x => x.Status == "Concern") ? "ReviewConcerns"
                : reasons.Any(x => x.Status == "Unknown") ? "IncompleteEvidence" : "SupportedHypothesis",
            reasons, efficiency, extension, room, spread, charges, reward,
            (entry - stop) * quantity, stop, target, sameDirectionPositions, recentLosses, bars.ToArray());
    }
}
