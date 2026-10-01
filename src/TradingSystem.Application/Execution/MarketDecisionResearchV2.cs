using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Execution;

public enum IntradayMarketStateV2
{
    Unavailable,
    BullTrend,
    BearTrend,
    StructuredRange,
    NoisyChop,
    Compression,
    Expansion,
    MatureOrExhausted,
    Transition
}

public sealed record MarketStateAssessmentV2(IntradayMarketStateV2 State,
    Direction? Bias, decimal Confidence, decimal DirectionalEfficiency,
    decimal DirectionFlipRate, decimal VwapCrossRate, decimal RangePosition,
    decimal EmaSeparationAtr, decimal MoveMaturityAtr, IReadOnlyList<string> Evidence);

public sealed record RangePlaybookDecisionV2(Direction? Direction, decimal Confidence,
    decimal Stop, decimal Target, IReadOnlyList<string> Reasons);

public sealed record DecisionFeatureSnapshotV2(string Version, string Market,
    DateTimeOffset CandleTimeUtc, decimal Price, string Strategy, Direction? CandidateDirection,
    decimal CandidateConfidence, MarketStateAssessmentV2 State,
    string SetupPhase, decimal Vwap, decimal FastEma, decimal SlowEma,
    decimal AtrPercent, decimal RelativeVolume, decimal OpeningRangeHigh,
    decimal OpeningRangeLow, decimal? ProposedStop, decimal? ProposedTarget,
    IReadOnlyList<string> SupportingEvidence, IReadOnlyList<string> FailedConditions);

public sealed record CounterfactualOutcomeV2(string Outcome, decimal MaximumFavourableExcursionR,
    decimal MaximumAdverseExcursionR, int BarsToOutcome);

public sealed record ProbabilityCalibrationBucketV2(int LowerPercent, int UpperPercent,
    int Observations, int Wins, decimal PredictedAverage, decimal ActualWinRate,
    decimal CalibrationError);

public sealed record StrategyValidationV2(string Strategy, int TrainingCandidates,
    decimal TrainingWinRate, int ValidationCandidates, decimal ValidationWinRate,
    string Status);

public sealed record DailyResearchPipelineReportV2(string Version, string Market,
    DateOnly SessionDate, int DecisionSnapshots, int ActionableCandidates,
    int CounterfactualWins, int CounterfactualLosses, int Unresolved,
    IReadOnlyDictionary<string, int> MarketStates,
    IReadOnlyList<ProbabilityCalibrationBucketV2> Calibration,
    IReadOnlyList<StrategyValidationV2> WalkForward,
    PaperPromotionDecision Promotion, DateTimeOffset GeneratedAtUtc,
    ResearchIntelligenceV3? Intelligence = null);

public interface IDailyResearchPipelineV2Reader
{
    Task<IReadOnlyList<DailyResearchPipelineReportV2>> GetLatestAsync(
        CancellationToken cancellationToken);
}

public static class MarketStateRouterV2
{
    public static MarketStateAssessmentV2 Analyze(IReadOnlyList<StrategyPriceBar> source,
        decimal vwap, decimal fastEma, decimal slowEma)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Count < 15 || vwap <= 0m)
            return new(IntradayMarketStateV2.Unavailable, null, 0m, 0m, 0m, 0m,
                .5m, 0m, 0m, ["At least 15 completed candles and VWAP are required."]);

        var bars = source.TakeLast(Math.Min(30, source.Count)).ToArray();
        var atr = Atr(bars.TakeLast(15).ToArray());
        if (atr <= 0m)
            return new(IntradayMarketStateV2.Unavailable, null, 0m, 0m, 0m, 0m,
                .5m, 0m, 0m, ["ATR is unavailable."]);
        var changes = bars.Zip(bars.Skip(1), (left, right) => right.Close - left.Close).ToArray();
        var travelled = changes.Sum(value => Math.Abs(value));
        var net = bars[^1].Close - bars[0].Close;
        var efficiency = travelled == 0m ? 0m : Math.Clamp(Math.Abs(net) / travelled, 0m, 1m);
        var signs = changes.Where(value => value != 0m).Select(Math.Sign).ToArray();
        var flipRate = signs.Length < 2 ? 0m : (decimal)signs.Zip(signs.Skip(1))
            .Count(value => value.First != value.Second) / (signs.Length - 1);
        var vwapSides = bars.Select(value => Math.Sign(value.Close - vwap))
            .Where(value => value != 0).ToArray();
        var crossRate = vwapSides.Length < 2 ? 0m : (decimal)vwapSides.Zip(vwapSides.Skip(1))
            .Count(value => value.First != value.Second) / (vwapSides.Length - 1);
        var high = bars.Max(value => value.High);
        var low = bars.Min(value => value.Low);
        var rangePosition = high == low ? .5m : (bars[^1].Close - low) / (high - low);
        var emaSeparation = Math.Abs(fastEma - slowEma) / atr;
        Direction? bias = net > 0m ? Direction.Buy : net < 0m ? Direction.Sell : null;
        var maturity = bias == Direction.Buy ? (bars[^1].Close - low) / atr :
            bias == Direction.Sell ? (high - bars[^1].Close) / atr : 0m;
        var recentAtr = Atr(bars.TakeLast(6).ToArray());
        var earlierAtr = Atr(bars.SkipLast(6).TakeLast(9).ToArray());
        var compression = earlierAtr > 0m && recentAtr / earlierAtr < .68m;
        var expansion = earlierAtr > 0m && recentAtr / earlierAtr > 1.45m;

        var state = maturity >= 3.2m && efficiency >= .42m
            ? IntradayMarketStateV2.MatureOrExhausted
            : flipRate >= .58m && efficiency <= .30m
                ? IntradayMarketStateV2.NoisyChop
                : compression
                    ? IntradayMarketStateV2.Compression
                    : expansion && efficiency >= .42m
                        ? IntradayMarketStateV2.Expansion
                        : efficiency >= .52m && emaSeparation >= .25m && bias == Direction.Buy
                            ? IntradayMarketStateV2.BullTrend
                            : efficiency >= .52m && emaSeparation >= .25m && bias == Direction.Sell
                                ? IntradayMarketStateV2.BearTrend
                                : efficiency <= .30m && crossRate <= .35m
                                    ? IntradayMarketStateV2.StructuredRange
                                    : IntradayMarketStateV2.Transition;
        var confidence = Math.Clamp(state switch
        {
            IntradayMarketStateV2.BullTrend or IntradayMarketStateV2.BearTrend =>
                .45m + efficiency * .35m + Math.Min(.15m, emaSeparation * .15m),
            IntradayMarketStateV2.NoisyChop => .50m + flipRate * .35m,
            IntradayMarketStateV2.StructuredRange => .50m + (1m - efficiency) * .25m,
            IntradayMarketStateV2.Compression or IntradayMarketStateV2.Expansion => .68m,
            IntradayMarketStateV2.MatureOrExhausted => .72m,
            _ => .50m
        }, 0m, .95m);
        return new(state, bias, confidence, efficiency, flipRate, crossRate, rangePosition,
            emaSeparation, maturity,
            [$"Efficiency {efficiency:P0}; direction flips {flipRate:P0}; VWAP crosses {crossRate:P0}.",
             $"EMA separation {emaSeparation:F2} ATR; move maturity {maturity:F2} ATR.",
             $"Price occupies {rangePosition:P0} of the observed range."]);
    }

    private static decimal Atr(StrategyPriceBar[] bars)
    {
        if (bars.Length < 2) return 0m;
        return bars.Skip(1).Select((bar, index) => Math.Max(bar.High - bar.Low,
            Math.Max(Math.Abs(bar.High - bars[index].Close),
                Math.Abs(bar.Low - bars[index].Close)))).Average();
    }
}

public static class RangePlaybookV2
{
    public const string NiftyStrategyCode = "Research|nifty-range-edge-rejection";
    public const string SensexStrategyCode = "Research|sensex-range-edge-rejection";
    public const string Version = "1.0.0";

    public static RangePlaybookDecisionV2 Evaluate(IReadOnlyList<StrategyPriceBar> source,
        MarketStateAssessmentV2 state)
    {
        if (source.Count < 15 || state.State != IntradayMarketStateV2.StructuredRange)
            return Reject("The market is not a confirmed structured range.");
        var bars = source.TakeLast(20).ToArray();
        var latest = bars[^1];
        var prior = bars.SkipLast(1).ToArray();
        var high = prior.Max(value => value.High);
        var low = prior.Min(value => value.Low);
        var width = high - low;
        if (width <= 0m) return Reject("The range has no usable width.");
        var position = (latest.Close - low) / width;
        if (position <= .22m && latest.Close > latest.Open && latest.Low <= low + width * .12m)
            return new(Direction.Buy, .70m, low - width * .08m, high - width * .15m,
                ["Lower range edge was tested and rejected.", "Entry is outside the range midpoint."]);
        if (position >= .78m && latest.Close < latest.Open && latest.High >= high - width * .12m)
            return new(Direction.Sell, .70m, high + width * .08m, low + width * .15m,
                ["Upper range edge was tested and rejected.", "Entry is outside the range midpoint."]);
        return Reject("Price is in the range middle or has not completed an edge rejection.");
    }

    private static RangePlaybookDecisionV2 Reject(string reason) => new(null, 0m, 0m, 0m, [reason]);
}

public static class CounterfactualOutcomeAnalyzerV2
{
    public static CounterfactualOutcomeV2 Evaluate(Direction direction, decimal entry,
        decimal stop, decimal target, IReadOnlyList<StrategyPriceBar> future)
    {
        var risk = Math.Abs(entry - stop);
        if (risk <= 0m || future.Count == 0) return new("Unavailable", 0m, 0m, 0);
        var mfe = 0m; var mae = 0m;
        for (var index = 0; index < future.Count; index++)
        {
            var favourable = direction == Direction.Buy ? future[index].High - entry :
                entry - future[index].Low;
            var adverse = direction == Direction.Buy ? entry - future[index].Low :
                future[index].High - entry;
            mfe = Math.Max(mfe, favourable / risk);
            mae = Math.Max(mae, adverse / risk);
            // If both occur inside one candle, conservatively record the stop first.
            var stopped = direction == Direction.Buy ? future[index].Low <= stop : future[index].High >= stop;
            var targeted = direction == Direction.Buy ? future[index].High >= target : future[index].Low <= target;
            if (stopped) return new("StopFirst", mfe, mae, index + 1);
            if (targeted) return new("TargetFirst", mfe, mae, index + 1);
        }
        return new("Unresolved", mfe, mae, future.Count);
    }
}

public static class ProbabilityCalibrationAnalyzerV2
{
    public static IReadOnlyList<ProbabilityCalibrationBucketV2> Analyze(
        IEnumerable<(decimal Probability, bool Won)> source)
    {
        return source.Where(value => value.Probability is >= 0m and <= 1m)
            .GroupBy(value => Math.Min(9, (int)(value.Probability * 10m)))
            .OrderBy(group => group.Key).Select(group =>
            {
                var count = group.Count();
                var predicted = group.Average(value => value.Probability);
                var wins = group.Count(value => value.Won);
                var actual = (decimal)wins / count;
                return new ProbabilityCalibrationBucketV2(group.Key * 10,
                    group.Key == 9 ? 100 : group.Key * 10 + 9, count, wins, predicted,
                    actual, Math.Abs(predicted - actual));
            }).ToArray();
    }
}
