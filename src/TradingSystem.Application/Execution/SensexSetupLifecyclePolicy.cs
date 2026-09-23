using TradingSystem.Application.MarketData;
using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Execution;

public enum SensexSetupPhase { Unavailable, Emerging, Pullback, EntryWindow, Extended, Invalidated }

public sealed record SensexSetupLifecycle(SensexSetupPhase Phase, Direction? Direction,
    int EstimatedAgeCandles, decimal AtrExtension, decimal EmaSeparationAtr,
    IReadOnlyList<string> Evidence);

public static class SensexSetupLifecyclePolicy
{
    public static SensexSetupLifecycle Evaluate(IReadOnlyList<StrategyPriceBar> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Count < 15)
            return new(SensexSetupPhase.Unavailable, null, 0, 0, 0,
                ["At least 15 completed candles are required."]);
        var bars = source.TakeLast(Math.Min(30, source.Count)).ToArray();
        var closes = bars.Select(x => x.Close).ToArray();
        var fast = TechnicalIndicators.ExponentialMovingAverage(closes, 9);
        var slow = TechnicalIndicators.ExponentialMovingAverage(closes, Math.Min(21, closes.Length));
        var atr = Atr(bars.TakeLast(15).ToArray());
        if (atr <= 0) return new(SensexSetupPhase.Unavailable, null, 0, 0, 0, ["ATR is unavailable."]);
        var direction = fast > slow ? Direction.Buy : fast < slow ? Direction.Sell : (Direction?)null;
        if (direction is null)
            return new(SensexSetupPhase.Invalidated, null, 0, 0, 0, ["EMA 9 and EMA 21 have no directional separation."]);
        var latest = bars[^1];
        var prior = bars.SkipLast(1).TakeLast(8).ToArray();
        var trigger = direction == Direction.Buy ? prior.Max(x => x.High) : prior.Min(x => x.Low);
        var extension = (direction == Direction.Buy ? latest.Close - trigger : trigger - latest.Close) / atr;
        var separation = Math.Abs(fast - slow) / atr;
        var alignedBars = bars.Reverse().TakeWhile(x => direction == Direction.Buy
            ? x.Close >= slow : x.Close <= slow).Count();
        var rejection = bars.Length >= 2 && (direction == Direction.Buy
            ? latest.Low <= fast + atr * .20m && latest.Close > latest.Open && latest.Close > bars[^2].Close
            : latest.High >= fast - atr * .20m && latest.Close < latest.Open && latest.Close < bars[^2].Close);
        var phase = extension > .65m ? SensexSetupPhase.Extended
            : rejection ? SensexSetupPhase.EntryWindow
            : direction == Direction.Buy ? latest.Low <= fast + atr * .25m ? SensexSetupPhase.Pullback : SensexSetupPhase.Emerging
            : latest.High >= fast - atr * .25m ? SensexSetupPhase.Pullback : SensexSetupPhase.Emerging;
        return new(phase, direction, alignedBars, extension, separation,
            [$"Setup has remained EMA-aligned for approximately {alignedBars} completed candle(s).",
             $"Price is {extension:F2} ATR beyond the previous eight-bar trigger.",
             rejection ? "A pullback rejection has opened an early entry window." : "No completed pullback rejection is currently present."]);
    }

    private static decimal Atr(StrategyPriceBar[] bars)
    {
        var ranges = new List<decimal>();
        for (var i = 1; i < bars.Length; i++)
            ranges.Add(Math.Max(bars[i].High - bars[i].Low,
                Math.Max(Math.Abs(bars[i].High - bars[i - 1].Close), Math.Abs(bars[i].Low - bars[i - 1].Close))));
        return ranges.Count == 0 ? 0 : ranges.Average();
    }
}
