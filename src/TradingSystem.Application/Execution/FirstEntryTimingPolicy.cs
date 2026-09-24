using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Execution;

public sealed record FirstEntryTimingDecision(bool Permitted, decimal AtrExtension,
    decimal ImpulseBodyAtr, IReadOnlyList<string> Reasons);

public static class FirstEntryTimingPolicy
{
    public static FirstEntryTimingDecision Evaluate(IReadOnlyList<StrategyPriceBar> source,
        Direction direction)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Count < 15) return new(false, 0, 0, ["At least 15 completed candles are required for entry timing."]);
        var bars = source.TakeLast(Math.Min(30, source.Count)).ToArray();
        var atr = Atr(bars.TakeLast(15).ToArray());
        if (atr <= 0) return new(false, 0, 0, ["ATR is unavailable for entry timing."]);
        var latest = bars[^1];
        var prior = bars.SkipLast(1).TakeLast(8).ToArray();
        var trigger = direction == Direction.Buy ? prior.Max(x => x.High) : prior.Min(x => x.Low);
        var extension = (direction == Direction.Buy ? latest.Close - trigger : trigger - latest.Close) / atr;
        var impulse = Math.Abs(latest.Close - latest.Open) / atr;
        var reasons = new List<string>();
        if (extension > .45m) reasons.Add($"Entry is {extension:F2} ATR beyond its eight-bar trigger; the move is already extended.");
        if (impulse > 1.10m) reasons.Add($"The latest candle body is {impulse:F2} ATR; entering after this impulse risks buying the exhaustion.");
        return new(reasons.Count == 0, extension, impulse,
            reasons.Count == 0 ? ["Entry remains inside the first-entry timing budget."] : reasons);
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
