using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Execution;

public sealed record ResearchCandidateV3(DecisionFeatureSnapshotV2 Snapshot,
    CounterfactualOutcomeV2 Outcome);

public sealed record EntryTimingResearchV3(int Candidates, int MatureMoveEntries,
    int EarlierOneBarWasBetter, int EarlierTwoBarsWasBetter, decimal AverageMoveMaturityAtr,
    decimal AverageMaximumFavourableExcursionR, decimal AverageMaximumAdverseExcursionR,
    IReadOnlyDictionary<string, decimal> AverageDirectionalReturnByHorizon);

public sealed record MissedMoveResearchV3(int NoCandidateSnapshots, int MaterialMissedMoves,
    IReadOnlyDictionary<string, int> LeadingBlockers);

public sealed record RegimeConfusionResearchV3(int Assessed, int Correct, int Incorrect,
    decimal Accuracy, IReadOnlyDictionary<string, int> ConfusionPairs);

public sealed record ResearchCohortV3(string Strategy, string MarketState, string Direction,
    string TimeBucket, int Candidates, int Wins, decimal WinRate,
    decimal AverageMfeR, decimal AverageMaeR);

public sealed record ExitResearchV3(int Assessed, int TargetFirst, int StopFirst,
    int ExtendedRunnerCandidates, int TightStopCandidates, decimal AverageMfeR,
    decimal AverageMaeR);

public sealed record ExecutionResearchV3(bool ChargesAppliedToExecutedTrades,
    bool BidAskAndSlippageAvailable, bool PartialFillSimulationAvailable, string Status);

public sealed record DataQualityResearchV3(int Candles, int ExpectedIntervalSeconds,
    int MissingIntervals, int DuplicateTimestamps, int NonPositivePrices,
    decimal Completeness, string Status);

public sealed record ResearchRecommendationV3(string Code, string Priority, string Finding,
    string ProposedExperiment, int SupportingObservations);

public sealed record ResearchIntelligenceV3(string Version, EntryTimingResearchV3 EntryTiming,
    MissedMoveResearchV3 MissedMoves, RegimeConfusionResearchV3 RegimeConfusion,
    IReadOnlyList<ResearchCohortV3> Cohorts, ExitResearchV3 Exits,
    ExecutionResearchV3 Execution, DataQualityResearchV3 DataQuality,
    IReadOnlyList<ResearchRecommendationV3> Recommendations,
    IReadOnlyList<string> Guardrails,
    AdvancedMarketIntelligenceV4? Advanced = null);

public static class ResearchIntelligenceAnalyzerV3
{
    private static readonly TimeSpan IndiaOffset = TimeSpan.FromMinutes(330);
    private static readonly int[] Horizons = [1, 3, 5, 10, 15];

    public static ResearchIntelligenceV3 Analyze(
        IReadOnlyList<DecisionFeatureSnapshotV2> snapshots,
        IReadOnlyList<ResearchCandidateV3> candidates,
        IReadOnlyList<StrategyPriceBar> candles,
        int expectedIntervalSeconds)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(candles);
        var ordered = candles.OrderBy(value => value.OpenTimeUtc).ToArray();
        var timing = Timing(candidates, ordered);
        var missed = Missed(snapshots, ordered);
        var confusion = Confusion(snapshots, ordered);
        var cohorts = candidates.GroupBy(value => new
            {
                value.Snapshot.Strategy,
                State = value.Snapshot.State.State.ToString(),
                Direction = value.Snapshot.CandidateDirection?.ToString() ?? "None",
                Time = TimeBucket(value.Snapshot.CandleTimeUtc)
            })
            .Select(group =>
            {
                var resolved = group.Where(value => IsResolved(value.Outcome)).ToArray();
                var wins = resolved.Count(value => value.Outcome.Outcome == "TargetFirst");
                return new ResearchCohortV3(group.Key.Strategy, group.Key.State,
                    group.Key.Direction, group.Key.Time, resolved.Length, wins,
                    resolved.Length == 0 ? 0m : (decimal)wins / resolved.Length,
                    Average(resolved.Select(value => value.Outcome.MaximumFavourableExcursionR)),
                    Average(resolved.Select(value => value.Outcome.MaximumAdverseExcursionR)));
            }).OrderByDescending(value => value.Candidates).Take(20).ToArray();
        var resolvedCandidates = candidates.Where(value => IsResolved(value.Outcome)).ToArray();
        var exits = new ExitResearchV3(resolvedCandidates.Length,
            resolvedCandidates.Count(value => value.Outcome.Outcome == "TargetFirst"),
            resolvedCandidates.Count(value => value.Outcome.Outcome == "StopFirst"),
            resolvedCandidates.Count(value => value.Outcome.MaximumFavourableExcursionR >= 1.5m),
            resolvedCandidates.Count(value => value.Outcome.Outcome == "StopFirst" &&
                value.Outcome.MaximumFavourableExcursionR >= .5m),
            Average(resolvedCandidates.Select(value => value.Outcome.MaximumFavourableExcursionR)),
            Average(resolvedCandidates.Select(value => value.Outcome.MaximumAdverseExcursionR)));
        var quality = Quality(ordered, expectedIntervalSeconds);
        var execution = new ExecutionResearchV3(true, false, false,
            "Executed-paper reports apply brokerage and statutory charges; candidate replay remains conservative because historical bid/ask, slippage and partial fills are unavailable.");
        return new("research-intelligence-v3", timing, missed, confusion, cohorts, exits,
            execution, quality, Recommendations(timing, missed, confusion, exits, quality),
            [
                "Research never changes intraday rules or promotes a strategy automatically.",
                "A challenger requires chronological out-of-sample evidence across multiple sessions.",
                "No P&L is invented where a historical option-price path is unavailable.",
                "Nifty and Sensex cohorts remain independent."
            ]);
    }

    private static EntryTimingResearchV3 Timing(IReadOnlyList<ResearchCandidateV3> source,
        StrategyPriceBar[] candles)
    {
        var oneEarlier = 0; var twoEarlier = 0;
        foreach (var candidate in source)
        {
            var index = Array.FindIndex(candles, value => value.OpenTimeUtc == candidate.Snapshot.CandleTimeUtc);
            if (index > 0 && BetterEarlier(candidate.Snapshot, candles[index - 1].Close)) oneEarlier++;
            if (index > 1 && BetterEarlier(candidate.Snapshot, candles[index - 2].Close)) twoEarlier++;
        }
        var horizons = Horizons.ToDictionary(value => $"{value}m", value =>
            Average(source.Select(candidate => DirectionalReturn(candidate.Snapshot, candles, value))));
        return new(source.Count,
            source.Count(value => value.Snapshot.State.State == IntradayMarketStateV2.MatureOrExhausted ||
                value.Snapshot.State.MoveMaturityAtr >= 3m), oneEarlier, twoEarlier,
            Average(source.Select(value => value.Snapshot.State.MoveMaturityAtr)),
            Average(source.Select(value => value.Outcome.MaximumFavourableExcursionR)),
            Average(source.Select(value => value.Outcome.MaximumAdverseExcursionR)), horizons);
    }

    private static MissedMoveResearchV3 Missed(IReadOnlyList<DecisionFeatureSnapshotV2> snapshots,
        StrategyPriceBar[] candles)
    {
        var rows = snapshots.Where(value => value.CandidateDirection is null).ToArray();
        var material = rows.Count(snapshot =>
        {
            var future = Future(snapshot.CandleTimeUtc, candles, 10);
            if (future.Length == 0 || snapshot.Price <= 0m) return false;
            var excursion = Math.Max(Math.Abs(future.Max(value => value.High) - snapshot.Price),
                Math.Abs(snapshot.Price - future.Min(value => value.Low)));
            var atr = snapshot.AtrPercent * snapshot.Price / 100m;
            return atr > 0m && excursion >= atr;
        });
        var blockers = rows.SelectMany(value => value.FailedConditions)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .GroupBy(value => value).OrderByDescending(value => value.Count()).Take(8)
            .ToDictionary(value => value.Key, value => value.Count());
        return new(rows.Length, material, blockers);
    }

    private static RegimeConfusionResearchV3 Confusion(
        IReadOnlyList<DecisionFeatureSnapshotV2> snapshots, StrategyPriceBar[] candles)
    {
        var pairs = new Dictionary<string, int>(); var correct = 0; var assessed = 0;
        foreach (var snapshot in snapshots)
        {
            var future = Future(snapshot.CandleTimeUtc, candles, 10);
            if (future.Length < 3 || snapshot.Price <= 0m) continue;
            var atr = snapshot.AtrPercent * snapshot.Price / 100m;
            if (atr <= 0m) continue;
            var net = future[^1].Close - snapshot.Price;
            var actual = Math.Abs(net) < atr * .45m ? "Range" : net > 0m ? "Bull" : "Bear";
            var predicted = snapshot.State.State switch
            {
                IntradayMarketStateV2.BullTrend => "Bull",
                IntradayMarketStateV2.BearTrend => "Bear",
                IntradayMarketStateV2.StructuredRange or IntradayMarketStateV2.NoisyChop or
                    IntradayMarketStateV2.Compression => "Range",
                _ => "Transition"
            };
            assessed++; if (predicted == actual || predicted == "Transition") correct++;
            var key = $"{predicted}→{actual}";
            pairs[key] = pairs.GetValueOrDefault(key) + 1;
        }
        return new(assessed, correct, assessed - correct,
            assessed == 0 ? 0m : (decimal)correct / assessed, pairs);
    }

    private static DataQualityResearchV3 Quality(StrategyPriceBar[] source, int intervalSeconds)
    {
        var duplicates = source.GroupBy(value => value.OpenTimeUtc).Count(value => value.Count() > 1);
        var missing = source.Zip(source.Skip(1)).Sum(pair =>
        {
            var seconds = (int)(pair.Second.OpenTimeUtc - pair.First.OpenTimeUtc).TotalSeconds;
            return seconds > intervalSeconds && seconds < 60 * 60
                ? Math.Max(0, seconds / Math.Max(1, intervalSeconds) - 1) : 0;
        });
        var invalid = source.Count(value => value.Open <= 0m || value.High <= 0m ||
            value.Low <= 0m || value.Close <= 0m);
        var expected = source.Length + missing;
        var completeness = expected == 0 ? 0m : (decimal)source.Length / expected;
        var status = invalid > 0 || duplicates > 0 ? "Invalid" :
            completeness < .98m ? "Incomplete" : "Healthy";
        return new(source.Length, intervalSeconds, missing, duplicates, invalid,
            completeness, status);
    }

    private static List<ResearchRecommendationV3> Recommendations(
        EntryTimingResearchV3 timing, MissedMoveResearchV3 missed,
        RegimeConfusionResearchV3 confusion, ExitResearchV3 exits,
        DataQualityResearchV3 quality)
    {
        var result = new List<ResearchRecommendationV3>();
        if (timing.MatureMoveEntries >= 3)
            result.Add(new("LATE_ENTRY", "High",
                $"{timing.MatureMoveEntries} candidates arrived after a mature move.",
                "Replay a maturity penalty and compare one/two-candle-earlier triggers.",
                timing.MatureMoveEntries));
        if (missed.MaterialMissedMoves >= 3)
            result.Add(new("MISSED_MOVE", "High",
                $"{missed.MaterialMissedMoves} blocked decisions preceded a move of at least one ATR.",
                "Replay the leading blocker as a confidence penalty instead of a hard veto.",
                missed.MaterialMissedMoves));
        if (confusion.Assessed >= 10 && confusion.Accuracy < .60m)
            result.Add(new("REGIME_CONFUSION", "High",
                $"Regime agreement with the next ten bars was {confusion.Accuracy:P0}.",
                "Challenge regime thresholds independently for this market.", confusion.Assessed));
        if (exits.TightStopCandidates >= 3)
            result.Add(new("EXIT_GIVEBACK", "Medium",
                $"{exits.TightStopCandidates} stopped candidates first achieved at least +0.5R.",
                "Replay break-even, structural trailing and partial-profit variants after costs.",
                exits.TightStopCandidates));
        if (quality.Status != "Healthy")
            result.Add(new("DATA_QUALITY", "Critical",
                $"Candle dataset is {quality.Status.ToLowerInvariant()} ({quality.Completeness:P1} complete).",
                "Exclude the affected session from promotion evidence and repair ingestion.",
                quality.MissingIntervals + quality.DuplicateTimestamps + quality.NonPositivePrices));
        if (result.Count == 0)
            result.Add(new("COLLECT_EVIDENCE", "Low",
                "No repeated defect crossed the experiment threshold today.",
                "Keep the current champion unchanged and collect more sessions.",
                timing.Candidates));
        return result;
    }

    private static bool BetterEarlier(DecisionFeatureSnapshotV2 snapshot, decimal earlier) =>
        snapshot.CandidateDirection == Direction.Buy ? earlier < snapshot.Price :
        snapshot.CandidateDirection == Direction.Sell && earlier > snapshot.Price;

    private static decimal DirectionalReturn(DecisionFeatureSnapshotV2 snapshot,
        StrategyPriceBar[] candles, int horizon)
    {
        if (snapshot.CandidateDirection is null || snapshot.Price <= 0m) return 0m;
        var future = Future(snapshot.CandleTimeUtc, candles, horizon);
        if (future.Length < horizon) return 0m;
        var change = (future[^1].Close - snapshot.Price) / snapshot.Price;
        return snapshot.CandidateDirection == Direction.Buy ? change : -change;
    }

    private static StrategyPriceBar[] Future(DateTimeOffset time, StrategyPriceBar[] candles,
        int count) => candles.Where(value => value.OpenTimeUtc > time).Take(count).ToArray();

    private static bool IsResolved(CounterfactualOutcomeV2 value) =>
        value.Outcome is "TargetFirst" or "StopFirst";

    private static string TimeBucket(DateTimeOffset utc)
    {
        var local = utc.ToOffset(IndiaOffset).TimeOfDay;
        return local < new TimeSpan(10, 30, 0) ? "Opening" :
            local < new TimeSpan(13, 30, 0) ? "Midday" : "Afternoon";
    }

    private static decimal Average(IEnumerable<decimal> source)
    {
        var values = source.ToArray(); return values.Length == 0 ? 0m : values.Average();
    }
}
