using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Strategies;

public sealed record TimeframeStructure(string Timeframe, Direction? Direction,
    decimal Strength, string State);

public sealed record MultiTimeframeDecisionAssessment(Direction? Consensus,
    bool CandidateAligned, string MarketLocation, decimal RemainingRoomAtr,
    IReadOnlyList<TimeframeStructure> Timeframes, IReadOnlyList<string> Evidence);

public static class MultiTimeframeDecisionPolicy
{
    private static readonly int[] FrameMinutes = [1, 5, 15];

    public static MultiTimeframeDecisionAssessment Analyze(
        IReadOnlyList<StrategyPriceBar> source, Direction? candidate,
        decimal openingRangeHigh, decimal openingRangeLow)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Count < 15)
            return new(null, false, "Unavailable", 0m, [],
                ["At least fifteen completed one-minute candles are required."]);
        var bars = source.OrderBy(value => value.OpenTimeUtc).ToArray();
        var frames = FrameMinutes.Select(minutes =>
            Assess(Aggregate(bars, minutes), $"{minutes}m")).ToArray();
        var bulls = frames.Count(value => value.Direction == Direction.Buy);
        var bears = frames.Count(value => value.Direction == Direction.Sell);
        Direction? consensus = bulls >= 2 ? Direction.Buy : bears >= 2 ? Direction.Sell : null;
        var latest = bars[^1];
        var atr = Atr(bars.TakeLast(15).ToArray());
        var range = openingRangeHigh - openingRangeLow;
        var position = range <= 0m ? .5m : Math.Clamp(
            (latest.Close - openingRangeLow) / range, 0m, 1m);
        var location = position <= .20m ? "Lower opening-range edge" :
            position >= .80m ? "Upper opening-range edge" : "Opening-range middle";
        var room = candidate switch
        {
            Direction.Buy when atr > 0m => Math.Max(0m, openingRangeHigh - latest.Close) / atr,
            Direction.Sell when atr > 0m => Math.Max(0m, latest.Close - openingRangeLow) / atr,
            _ => 0m
        };
        var aligned = candidate is not null && consensus == candidate &&
                      !frames.Any(value => value.Timeframe == "15m" &&
                          value.Direction is not null && value.Direction != candidate && value.Strength >= .55m);
        return new(consensus, aligned, location, room, frames,
            [$"Multi-timeframe consensus is {consensus?.ToString() ?? "mixed"}; candidate alignment is {aligned}.",
             $"Price location: {location}; remaining room is {room:F2} ATR.",
             string.Join(" · ", frames.Select(value =>
                 $"{value.Timeframe} {value.State} ({value.Strength:P0})"))]);
    }

    private static TimeframeStructure Assess(StrategyPriceBar[] bars, string timeframe)
    {
        if (bars.Length < 4) return new(timeframe, null, 0m, "Waiting");
        var closes = bars.Select(value => value.Close).ToArray();
        var fast = Ema(closes, Math.Min(5, closes.Length));
        var slow = Ema(closes, Math.Min(10, closes.Length));
        var travelled = closes.Zip(closes.Skip(1), (left, right) => Math.Abs(right - left)).Sum();
        var net = closes[^1] - closes[0];
        var strength = travelled <= 0m ? 0m : Math.Clamp(Math.Abs(net) / travelled, 0m, 1m);
        Direction? direction = fast > slow && net > 0m ? Direction.Buy :
            fast < slow && net < 0m ? Direction.Sell : null;
        var state = direction is null ? "Neutral" : direction == Direction.Buy ? "Bullish" : "Bearish";
        return new(timeframe, direction, strength, state);
    }

    private static StrategyPriceBar[] Aggregate(StrategyPriceBar[] source, int minutes)
    {
        if (minutes == 1) return source;
        return source.GroupBy(value => value.OpenTimeUtc.ToUnixTimeSeconds() / (minutes * 60L))
            .OrderBy(group => group.Key).Select(group =>
            {
                var values = group.OrderBy(value => value.OpenTimeUtc).ToArray();
                return new StrategyPriceBar(values[0].OpenTimeUtc, values[0].Open,
                    values.Max(value => value.High), values.Min(value => value.Low), values[^1].Close);
            }).ToArray();
    }

    private static decimal Ema(decimal[] values, int period)
    {
        var multiplier = 2m / (period + 1m); var result = values[0];
        foreach (var value in values.Skip(1)) result = value * multiplier + result * (1m - multiplier);
        return result;
    }

    private static decimal Atr(StrategyPriceBar[] bars) => bars.Length < 2 ? 0m :
        bars.Skip(1).Select((bar, index) => Math.Max(bar.High - bar.Low,
            Math.Max(Math.Abs(bar.High - bars[index].Close),
                Math.Abs(bar.Low - bars[index].Close)))).Average();
}
