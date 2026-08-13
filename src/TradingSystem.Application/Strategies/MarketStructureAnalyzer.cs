namespace TradingSystem.Application.Strategies;

public static class MarketStructureAnalyzer
{
    public static MarketStructureSnapshot Analyze(IReadOnlyList<StrategyPriceBar> bars)
    {
        ArgumentNullException.ThrowIfNull(bars);
        if (bars.Count < 6) return MarketStructureSnapshot.Unavailable;

        var recent = bars.TakeLast(Math.Min(12, bars.Count)).ToArray();
        var mid = recent.Length / 2;
        var first = recent[..mid];
        var second = recent[mid..];
        var higherHigh = second.Max(x => x.High) > first.Max(x => x.High);
        var higherLow = second.Min(x => x.Low) > first.Min(x => x.Low);
        var lowerHigh = second.Max(x => x.High) < first.Max(x => x.High);
        var lowerLow = second.Min(x => x.Low) < first.Min(x => x.Low);
        var bullishCloses = recent.Zip(recent.Skip(1)).Count(x => x.Second.Close > x.First.Close);
        var bearishCloses = recent.Zip(recent.Skip(1)).Count(x => x.Second.Close < x.First.Close);
        var comparisons = recent.Length - 1;

        var direction = higherHigh && higherLow ? MarketStructureDirection.Bullish
            : lowerHigh && lowerLow ? MarketStructureDirection.Bearish
            : MarketStructureDirection.Range;
        var directionalCloses = direction == MarketStructureDirection.Bullish ? bullishCloses
            : direction == MarketStructureDirection.Bearish ? bearishCloses
            : Math.Max(bullishCloses, bearishCloses);
        var strength = direction == MarketStructureDirection.Range
            ? 1m - (decimal)Math.Abs(bullishCloses - bearishCloses) / comparisons
            : (decimal)directionalCloses / comparisons;

        return new(direction, Math.Clamp(strength, 0m, 1m),
            second.Max(x => x.High), second.Min(x => x.Low), directionalCloses);
    }
}
