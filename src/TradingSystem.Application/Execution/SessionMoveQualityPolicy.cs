using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Execution;

public sealed record SessionMoveQualityDecision(bool Permitted, decimal PeakMoveAtr,
    decimal CurrentMoveAtr, decimal DistanceFromExtremeAtr, IReadOnlyList<string> Reasons);

/// <summary>
/// Prevents chasing a mature session impulse at its extreme while leaving fresh pullback
/// and continuation structures eligible. It intentionally has no index-specific point cutoffs.
/// </summary>
public static class SessionMoveQualityPolicy
{
    public const string Version = "session-move-causal-nifty-v2";
    public const decimal MatureMoveAtr = 2.5m;
    public const decimal NearExtremeAtr = .35m;
    public const decimal RequiredPullbackAtr = .35m;

    public static SessionMoveQualityDecision Evaluate(
        IReadOnlyList<StrategyPriceBar> fullSession,
        Direction direction,
        decimal atr, bool relaxedNiftyEntries = false)
    {
        ArgumentNullException.ThrowIfNull(fullSession);
        if (fullSession.Count < 4 || atr <= 0m)
            return new(true, 0m, 0m, 0m,
                ["Full-session move assessment is unavailable; existing entry checks remain in force."]);

        var open = fullSession[0].Open;
        var latest = fullSession[^1];
        var peakMove = direction == Direction.Buy
            ? fullSession.Max(value => value.High) - open
            : open - fullSession.Min(value => value.Low);
        var currentMove = direction == Direction.Buy
            ? latest.Close - open
            : open - latest.Close;
        var sessionExtreme = direction == Direction.Buy
            ? fullSession.Max(value => value.High)
            : fullSession.Min(value => value.Low);
        var distanceFromExtreme = direction == Direction.Buy
            ? sessionExtreme - latest.Close
            : latest.Close - sessionExtreme;
        var peakMoveAtr = Math.Max(0m, peakMove / atr);
        var currentMoveAtr = currentMove / atr;
        var distanceFromExtremeAtr = Math.Max(0m, distanceFromExtreme / atr);

        if (currentMove <= 0m || peakMoveAtr < (relaxedNiftyEntries ? 3.5m : MatureMoveAtr) ||
            distanceFromExtremeAtr > NearExtremeAtr)
            return new(true, peakMoveAtr, currentMoveAtr, distanceFromExtremeAtr,
                ["The candidate is not chasing a mature same-direction session extreme."]);

        var recent = fullSession.TakeLast(4).ToArray();
        var pullbackBars = recent.SkipLast(1).ToArray();
        var requiredPullback = atr * (relaxedNiftyEntries ? .20m : RequiredPullbackAtr);
        var pullbackObserved = direction == Direction.Buy
            ? pullbackBars.Any(value => value.Low <= sessionExtreme - requiredPullback)
            : pullbackBars.Any(value => value.High >= sessionExtreme + requiredPullback);
        if (relaxedNiftyEntries)
        {
            // A low before a later high is not a pullback from that high. Require
            // an actual completed retracement followed by its FIRST pivot
            // reclaim. Reuse the existing retest strategy's six-bar horizon;
            // an old confirmation cannot become a fresh entry at each new high.
            // The already-shared two-close range acceptance keeps orderly
            // breakouts eligible without demanding that every trend retrace.
            var tail = fullSession.TakeLast(8).ToArray();
            var interval = tail.Length >= 2 ? tail[^1].OpenTimeUtc - tail[^2].OpenTimeUtc : TimeSpan.Zero;
            var contiguous = tail.Length == 8 && interval.TotalSeconds is 60 or 300 &&
                tail.Zip(tail.Skip(1)).All(p => p.Second.OpenTimeUtc - p.First.OpenTimeUtc == interval) &&
                tail[0].OpenTimeUtc.ToOffset(TimeSpan.FromHours(5.5)).Date ==
                    tail[^1].OpenTimeUtc.ToOffset(TimeSpan.FromHours(5.5)).Date;
            var sign = direction == Direction.Buy ? 1m : -1m;
            bool FirstReclaim(int i)
            {
                var pivot = direction == Direction.Buy ? tail[i].High : tail[i].Low;
                return sign * (tail[i - 1].Close - tail[i].Close) >= requiredPullback &&
                    sign * (latest.Close - pivot) > 0m &&
                    tail.Skip(i + 1).SkipLast(1).All(b => sign * (b.Close - pivot) <= 0m);
            }
            pullbackObserved = contiguous && (Enumerable.Range(1, 6).Any(FirstReclaim) ||
                SharedMarketContextPolicy.AcceptedBreak(fullSession, direction));
        }
        var resumed = direction == Direction.Buy
            ? latest.Close > latest.Open && latest.Close > pullbackBars.Max(value => value.High)
            : latest.Close < latest.Open && latest.Close < pullbackBars.Min(value => value.Low);

        return pullbackObserved && resumed
            ? new(true, peakMoveAtr, currentMoveAtr, distanceFromExtremeAtr,
                [relaxedNiftyEntries
                    ? "A mature session move has a first completed pullback reclaim or two-close breakout acceptance, with direction-aligned resumption."
                    : "A mature session move has a completed pullback and direction-aligned resumption trigger."])
            : new(false, peakMoveAtr, currentMoveAtr, distanceFromExtremeAtr,
                [$"Same-direction session move reached {peakMoveAtr:F2} ATR and price is within " +
                 $"{distanceFromExtremeAtr:F2} ATR of its extreme; wait for a completed pullback and resumption."]);
    }
}
