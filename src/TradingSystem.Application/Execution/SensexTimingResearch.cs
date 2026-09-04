using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Execution;

// Observational only. These thresholds are hypotheses, never entry gates.
public static class SensexTimingResearch
{
    public static object Observe(IReadOnlyList<StrategyPriceBar> bars, Direction direction,
        decimal atr, DateTimeOffset observedAtUtc, int intervalSeconds)
    {
        var latest = bars[^1];
        var prior = bars.SkipLast(1).TakeLast(8).ToArray();
        decimal? reference = prior.Length == 0 ? null : direction == Direction.Buy
            ? prior.Max(x => x.High) : prior.Min(x => x.Low);
        decimal? extension = atr <= 0 || reference is null ? null :
            (direction == Direction.Buy ? latest.Close - reference : reference - latest.Close) / atr;
        var opposing = direction == Direction.Buy
            ? bars.SkipLast(1).Where(x => x.High > latest.Close).Select(x => (decimal?)x.High).Min()
            : bars.SkipLast(1).Where(x => x.Low < latest.Close).Select(x => (decimal?)x.Low).Max();
        decimal? room = atr <= 0 || opposing is null ? null : Math.Abs(opposing.Value - latest.Close) / atr;
        var candleAge = (observedAtUtc - latest.OpenTimeUtc.AddSeconds(intervalSeconds)).TotalSeconds;
        return new
        {
            version = "sensex-timing-v1", advisoryOnly = true, direction = direction.ToString(),
            observedAtUtc, latest.OpenTimeUtc, underlyingPrice = latest.Close, atr,
            candleCloseAgeSeconds = candleAge, setupAgeSeconds = (double?)null,
            referenceKind = "previous-eight-bar-extreme-proxy", reference,
            extensionAtr = extension, nearestObservedOpposingLevel = opposing, roomAtr = room,
            staleCandleHypothesis = candleAge > intervalSeconds * 2,
            extendedHypothesis = extension is null ? (bool?)null : extension > 1m,
            limitedRoomHypothesis = room is null ? (bool?)null : room < 0.5m,
            limitation = "Local observed levels only; no claim of major support/resistance or first setup detection."
        };
    }
}
