using TradingSystem.Application.MarketData;
using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Execution;

public sealed record StructuralReentryDecision(bool Permitted, bool NewStructureObserved,
    int CompletedBarsSinceExit, IReadOnlyList<string> Reasons);

public static class StructuralReentryPolicy
{
    public static StructuralReentryDecision Evaluate(IReadOnlyList<StrategyPriceBar> source,
        Direction direction, DateTimeOffset? previousExitUtc)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (previousExitUtc is null)
            return new(true, true, 0, ["No prior same-direction exit exists in this session."]);

        var afterExit = source.Where(x => x.OpenTimeUtc > previousExitUtc.Value).ToArray();
        if (afterExit.Length < 3)
            return new(false, false, afterExit.Length,
                [$"Only {afterExit.Length} completed candle(s) exist after the previous same-direction exit; a fresh structure has not formed."]);

        var closes = source.Select(x => x.Close).ToArray();
        var fast = TechnicalIndicators.ExponentialMovingAverage(closes, Math.Min(9, closes.Length));
        var latest = afterExit[^1];
        var prior = afterExit[^2];
        var reset = direction == Direction.Buy
            ? afterExit.Any(x => x.Low <= fast) && latest.Close > fast &&
              latest.Close > latest.Open && latest.Close > prior.Close
            : afterExit.Any(x => x.High >= fast) && latest.Close < fast &&
              latest.Close < latest.Open && latest.Close < prior.Close;
        return reset
            ? new(true, true, afterExit.Length,
                ["A completed pullback and rejection formed after the previous same-direction exit."])
            : new(false, false, afterExit.Length,
                ["Time alone is insufficient: no completed EMA pullback-and-rejection reset exists after the previous same-direction exit."]);
    }
}
