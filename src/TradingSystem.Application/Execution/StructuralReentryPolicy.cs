using TradingSystem.Application.MarketData;
using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Execution;

public sealed record StructuralReentryDecision(bool Permitted, bool NewStructureObserved,
    int CompletedBarsSinceExit, IReadOnlyList<string> Reasons);

public static class StructuralReentryPolicy
{
    public static StructuralReentryDecision Evaluate(IReadOnlyList<StrategyPriceBar> source,
        Direction direction, DateTimeOffset? previousExitUtc,
        bool persistentDirectionalContext = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (previousExitUtc is null)
            return new(true, true, 0, ["No prior same-direction exit exists in this session."]);

        var afterExit = source.Where(x => x.OpenTimeUtc > previousExitUtc.Value).ToArray();
        // First entries remain untouched. After a persistent trend entry exits, require
        // a full pullback/base plus a separate completed trigger candle before re-entry.
        var minimumBars = persistentDirectionalContext ? 4 : 5;
        if (afterExit.Length < minimumBars)
            return new(false, false, afterExit.Length,
                [$"Only {afterExit.Length} completed candle(s) exist after the previous same-direction exit; a fresh structure has not formed."]);

        var closes = source.Select(x => x.Close).ToArray();
        var fast = TechnicalIndicators.ExponentialMovingAverage(closes, Math.Min(9, closes.Length));
        var atr = Atr(source.TakeLast(Math.Min(15, source.Count)).ToArray());
        var latest = afterExit[^1];
        var prior = afterExit[^2];
        var meaningfulRange = atr > 0 && afterExit.Max(x => x.High) - afterExit.Min(x => x.Low) >= atr * .75m;
        var reset = direction == Direction.Buy
            ? afterExit.SkipLast(1).Any(x => x.Low <= fast - atr * .15m) && meaningfulRange && latest.Close > fast &&
              latest.Close > latest.Open && latest.Close > prior.Close
            : afterExit.SkipLast(1).Any(x => x.High >= fast + atr * .15m) && meaningfulRange && latest.Close < fast &&
              latest.Close < latest.Open && latest.Close < prior.Close;
        var continuationBase = false;
        if (persistentDirectionalContext && afterExit.Length >= 4 && atr > 0m)
        {
            var baseBars = afterExit.TakeLast(4).ToArray();
            var baseRange = baseBars.SkipLast(1).Max(x => x.High) -
                            baseBars.SkipLast(1).Min(x => x.Low);
            var pauseObserved = baseBars.SkipLast(1).Any(x => direction == Direction.Buy
                ? x.Close <= x.Open : x.Close >= x.Open);
            continuationBase = pauseObserved && baseRange <= atr * 1.10m && meaningfulRange &&
                (direction == Direction.Buy
                    ? latest.Close > baseBars.SkipLast(1).Max(x => x.High) &&
                      latest.Close > latest.Open && latest.Close > fast
                    : latest.Close < baseBars.SkipLast(1).Min(x => x.Low) &&
                      latest.Close < latest.Open && latest.Close < fast);
        }
        return reset || continuationBase
            ? new(true, true, afterExit.Length,
                [continuationBase
                    ? "A compact continuation base and direction-aligned break formed after the previous exit."
                    : "A completed pullback and rejection formed after the previous same-direction exit."])
            : new(false, false, afterExit.Length,
                ["Time alone is insufficient: no completed EMA pullback-and-rejection reset exists after the previous same-direction exit."]);
    }

    private static decimal Atr(StrategyPriceBar[] bars)
    {
        if (bars.Length < 2) return 0;
        var ranges = new List<decimal>();
        for (var i = 1; i < bars.Length; i++)
            ranges.Add(Math.Max(bars[i].High - bars[i].Low,
                Math.Max(Math.Abs(bars[i].High - bars[i - 1].Close), Math.Abs(bars[i].Low - bars[i - 1].Close))));
        return ranges.Average();
    }
}
