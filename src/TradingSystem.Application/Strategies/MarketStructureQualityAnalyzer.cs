using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Strategies;

public enum MarketStructureQualityState
{
    Unavailable,
    CleanTrend,
    DevelopingTrend,
    MatureTrend,
    StructuredRange,
    NoisyChop,
    VolatilityTransition
}

public sealed record MarketStructureQualitySnapshot(MarketStructureQualityState State,
    Direction? ObservedBias, decimal TrendEfficiency, decimal ChopScore,
    decimal SwingConsistency, decimal MoveMaturityAtr, decimal? RoomToRiskRatio,
    decimal TrendQuality, bool WouldPermit, IReadOnlyList<string> Reasons)
{
    public static MarketStructureQualitySnapshot Unavailable { get; } = new(
        MarketStructureQualityState.Unavailable, null, 0m, 1m, 0m, 0m, null, 0m, false,
        ["At least twelve completed candles are required for structure-quality analysis."]);
}

public static class MarketStructureQualityAnalyzer
{
    public static MarketStructureQualitySnapshot Analyze(IReadOnlyList<StrategyPriceBar> bars,
        Direction? candidateDirection, decimal currentPrice, decimal vwap, decimal atrPercent,
        decimal openingRangeHigh, decimal openingRangeLow, decimal? proposedRisk = null)
    {
        ArgumentNullException.ThrowIfNull(bars);
        if (bars.Count < 12 || currentPrice <= 0 || vwap <= 0 || atrPercent <= 0)
            return MarketStructureQualitySnapshot.Unavailable;

        var recent = bars.TakeLast(Math.Min(20, bars.Count)).ToArray();
        var changes = recent.Zip(recent.Skip(1), (left, right) => right.Close - left.Close).ToArray();
        var travelled = changes.Sum(value => Math.Abs(value));
        var net = recent[^1].Close - recent[0].Close;
        var efficiency = travelled <= 0 ? 0m : Math.Clamp(Math.Abs(net) / travelled, 0m, 1m);
        var nonZeroSigns = changes.Where(value => value != 0).Select(Math.Sign).ToArray();
        var flips = nonZeroSigns.Zip(nonZeroSigns.Skip(1)).Count(value => value.First != value.Second);
        var flipRatio = nonZeroSigns.Length <= 1 ? 0m : (decimal)flips / (nonZeroSigns.Length - 1);
        var vwapSides = recent.Select(value => Math.Sign(value.Close - vwap)).Where(value => value != 0).ToArray();
        var vwapCrosses = vwapSides.Zip(vwapSides.Skip(1)).Count(value => value.First != value.Second);
        var vwapCrossRatio = vwapSides.Length <= 1 ? 0m : (decimal)vwapCrosses / (vwapSides.Length - 1);
        var chop = Math.Clamp((1m - efficiency) * 0.45m + flipRatio * 0.35m +
                              vwapCrossRatio * 0.20m, 0m, 1m);

        Direction? observedBias = net > 0 ? Direction.Buy : net < 0 ? Direction.Sell : null;
        var directionalComparisons = recent.Zip(recent.Skip(1)).Select(value => observedBias switch
        {
            Direction.Buy => value.Second.High > value.First.High && value.Second.Low >= value.First.Low,
            Direction.Sell => value.Second.High <= value.First.High && value.Second.Low < value.First.Low,
            _ => false
        }).ToArray();
        var swingConsistency = directionalComparisons.Length == 0 ? 0m :
            (decimal)directionalComparisons.Count(value => value) / directionalComparisons.Length;
        var trendQuality = Math.Clamp(efficiency * 0.50m + swingConsistency * 0.30m +
                                      (1m - chop) * 0.20m, 0m, 1m);
        var atr = currentPrice * atrPercent / 100m;
        var directionForMaturity = candidateDirection ?? observedBias;
        var maturity = directionForMaturity switch
        {
            Direction.Buy => (currentPrice - recent.Min(value => value.Low)) / atr,
            Direction.Sell => (recent.Max(value => value.High) - currentPrice) / atr,
            _ => 0m
        };
        maturity = Math.Max(0m, maturity);
        var room = KnownRoom(candidateDirection, currentPrice, openingRangeHigh, openingRangeLow,
            recent, proposedRisk);

        var aligned = candidateDirection is null || observedBias is null || candidateDirection == observedBias;
        var state = chop >= 0.62m && efficiency <= 0.40m
            ? MarketStructureQualityState.NoisyChop
            : maturity >= 3m && aligned && trendQuality >= 0.45m
                ? MarketStructureQualityState.MatureTrend
                : aligned && efficiency >= 0.50m && chop <= 0.40m && swingConsistency >= 0.50m
                    ? MarketStructureQualityState.CleanTrend
                    : aligned && efficiency >= 0.35m && chop < 0.58m
                        ? MarketStructureQualityState.DevelopingTrend
                        : efficiency <= 0.25m && chop < 0.62m
                            ? MarketStructureQualityState.StructuredRange
                            : MarketStructureQualityState.VolatilityTransition;

        var reasons = new List<string>
        {
            $"Trend efficiency {efficiency:P0}; chop {chop:P0}; swing consistency {swingConsistency:P0}.",
            $"Directional move maturity is {maturity:F2} ATR."
        };
        var permit = state != MarketStructureQualityState.Unavailable;
        if (state == MarketStructureQualityState.NoisyChop)
        {
            permit = false;
            reasons.Add("Frequent direction changes and low path efficiency indicate noisy chop.");
        }
        if (state == MarketStructureQualityState.MatureTrend)
        {
            permit = false;
            reasons.Add("The directional move is mature; a continuation entry risks chasing exhaustion.");
        }
        if (!aligned && trendQuality >= 0.55m)
        {
            permit = false;
            reasons.Add("The candidate conflicts with the observed high-quality directional path.");
        }
        if (room is < 1.10m)
        {
            permit = false;
            reasons.Add($"Known room before structure is only {room:F2}R.");
        }
        if (permit) reasons.Add("No shadow structure-quality veto was triggered.");
        return new(state, observedBias, efficiency, chop, swingConsistency, maturity, room,
            trendQuality, permit, reasons);
    }

    private static decimal? KnownRoom(Direction? direction, decimal currentPrice,
        decimal openingRangeHigh, decimal openingRangeLow, IReadOnlyList<StrategyPriceBar> bars,
        decimal? proposedRisk)
    {
        if (direction is null || proposedRisk is null or <= 0) return null;
        var levels = direction == Direction.Buy
            ? bars.SkipLast(1).Select(value => value.High).Append(openingRangeHigh)
                .Where(value => value > currentPrice).ToArray()
            : bars.SkipLast(1).Select(value => value.Low).Append(openingRangeLow)
                .Where(value => value < currentPrice).ToArray();
        if (levels.Length == 0) return null;
        var distance = direction == Direction.Buy ? levels.Min() - currentPrice :
            currentPrice - levels.Max();
        return Math.Max(0m, distance / proposedRisk.Value);
    }
}
