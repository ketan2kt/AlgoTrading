namespace TradingSystem.Application.Execution;

public sealed record ReplayFrameV5(DateTimeOffset TimeUtc, decimal Price, string MarketState,
    string SetupPhase, string Candidate, decimal Confidence,
    IReadOnlyList<string> Reasons);
public sealed record StrategyComparisonV5(string Strategy, string MarketState,
    string Direction, string TimeBucket, int Candidates, decimal WinRate,
    decimal ExpectancyR, decimal ProfitFactorR, string Verdict);
public sealed record ParameterTrialV5(decimal MinimumConfidence, decimal MaximumMaturityAtr,
    int Candidates, int Wins, decimal WinRate, decimal ExpectancyR, string Status);
public sealed record MonteCarloV5(int Simulations, int TradesPerSimulation,
    decimal MedianOutcomeR, decimal FifthPercentileOutcomeR,
    decimal NinetyFifthPercentileDrawdownR, decimal LosingRunProbability,
    string Evidence);
public sealed record OperationalAlertV5(string Severity, string Code, string Message);
public sealed record ProvenanceV5(string BuildVersion, string ConfigurationChecksum,
    IReadOnlyList<string> StrategyVersions, IReadOnlyList<string> DataSources);
public sealed record RetentionExportV5(string HotRetention, string ArchiveRetention,
    IReadOnlyList<string> ExportFormats, string Status);
public sealed record ReleaseGovernanceV5(string CurrentStage, string NextStage,
    bool AdvancementPermitted, IReadOnlyList<string> Gates);
public sealed record ResearchOperationsV5(string Version, string BackfillStatus,
    IReadOnlyList<ReplayFrameV5> ReplayFrames,
    IReadOnlyList<StrategyComparisonV5> StrategyComparison,
    IReadOnlyList<string> DailyDigest, IReadOnlyList<string> WeeklyConsolidation,
    IReadOnlyList<ParameterTrialV5> ParameterSearch, MonteCarloV5 MonteCarlo,
    IReadOnlyList<OperationalAlertV5> Alerts, ProvenanceV5 Provenance,
    RetentionExportV5 Retention, ReleaseGovernanceV5 Governance);

public static class ResearchOperationsAnalyzerV5
{
    private static readonly decimal[] ConfidenceThresholds = [.55m, .65m, .75m];
    private static readonly decimal[] MaturityThresholds = [2m, 3m, 4m];

    public static ResearchOperationsV5 Analyze(
        IReadOnlyList<DecisionFeatureSnapshotV2> current,
        IReadOnlyList<DecisionFeatureSnapshotV2> baseline,
        IReadOnlyList<ResearchCandidateV3> candidates,
        ResearchIntelligenceV3 intelligence, string buildVersion,
        string configurationChecksum)
    {
        var frames = current.OrderBy(value => value.CandleTimeUtc).Select(value =>
            new ReplayFrameV5(value.CandleTimeUtc, value.Price, value.State.State.ToString(),
                value.SetupPhase, value.CandidateDirection?.ToString() ?? "None",
                value.CandidateConfidence,
                value.FailedConditions.Count > 0 ? value.FailedConditions : value.SupportingEvidence))
            .ToArray();
        var comparison = intelligence.Cohorts.Select(value =>
        {
            var losses = value.Candidates - value.Wins;
            var expectancy = value.Candidates == 0 ? 0m :
                ((decimal)value.Wins - losses) / value.Candidates;
            var factor = losses == 0 ? value.Wins > 0 ? 99m : 0m : (decimal)value.Wins / losses;
            var verdict = value.Candidates < 30 ? "CollectEvidence" :
                value.WinRate >= .70m && expectancy > 0m ? "Candidate" : "Rejected";
            return new StrategyComparisonV5(value.Strategy, value.MarketState,
                value.Direction, value.TimeBucket, value.Candidates, value.WinRate,
                expectancy, factor, verdict);
        }).ToArray();
        var trials = (from confidence in ConfidenceThresholds
                      from maturity in MaturityThresholds
                      let rows = candidates.Where(value =>
                          value.Snapshot.CandidateConfidence >= confidence &&
                          value.Snapshot.State.MoveMaturityAtr <= maturity &&
                          value.Outcome.Outcome is "TargetFirst" or "StopFirst").ToArray()
                      let wins = rows.Count(value => value.Outcome.Outcome == "TargetFirst")
                      let winRate = rows.Length == 0 ? 0m : (decimal)wins / rows.Length
                      let expectancy = rows.Length == 0 ? 0m :
                          (decimal)(wins - (rows.Length - wins)) / rows.Length
                      select new ParameterTrialV5(confidence, maturity, rows.Length, wins,
                          winRate, expectancy, rows.Length < 30 ? "CollectEvidence" :
                              winRate >= .70m && expectancy > 0m ? "Candidate" : "Rejected"))
            .ToArray();
        var monteCarlo = MonteCarlo(candidates);
        var alerts = Alerts(current, intelligence);
        var daily = new List<string>
        {
            $"{current.Count} decisions and {candidates.Count} actionable candidates were recorded.",
            $"Late/mature candidates: {intelligence.EntryTiming.MatureMoveEntries}; material missed moves: {intelligence.MissedMoves.MaterialMissedMoves}.",
            $"Regime agreement: {intelligence.RegimeConfusion.Accuracy:P0}; data quality: {intelligence.DataQuality.Status}.",
            $"Best bounded trial: {BestTrial(trials)}"
        };
        var weeklyStates = baseline.Concat(current).GroupBy(value => value.State.State.ToString())
            .OrderByDescending(value => value.Count()).Take(5)
            .Select(value => $"{value.Key}: {value.Count()} decision(s)").ToArray();
        var strategyVersions = current.Select(value => value.Strategy).Distinct().Order().ToArray();
        var provenance = new ProvenanceV5(buildVersion, configurationChecksum,
            strategyVersions, ["Groww official candles", "Groww official option chain",
                "Persisted paper broker journal", "PaperTradingCostModel"]);
        var retention = new RetentionExportV5("Raw one-minute and option snapshots: 90 days",
            "Daily research reports and trade outcomes: indefinite",
            ["JSON", "CSV"], "Database-backed export ready; archive execution requires operator action.");
        var candidateReady = comparison.Any(value => value.Verdict == "Candidate") &&
            trials.Any(value => value.Status == "Candidate") &&
            intelligence.DataQuality.Status == "Healthy" && alerts.All(value => value.Severity != "Critical");
        var governance = new ReleaseGovernanceV5("OfflineReplay", "Shadow",
            candidateReady, ["Offline replay", "Shadow", "Paper challenger", "Paper champion",
                "Optional live canary", "No stage may be skipped automatically"]);
        return new("research-operations-v5", "Official Groww historical-candle gateway ready; backfill remains operator-initiated and idempotent.",
            frames, comparison, daily, weeklyStates, trials, monteCarlo, alerts,
            provenance, retention, governance);
    }

    private static MonteCarloV5 MonteCarlo(IReadOnlyList<ResearchCandidateV3> candidates)
    {
        var outcomes = candidates.Where(value => value.Outcome.Outcome is "TargetFirst" or "StopFirst")
            .Select(value => value.Outcome.Outcome == "TargetFirst" ? 1m : -1m).ToArray();
        if (outcomes.Length < 5) return new(0, outcomes.Length, 0m, 0m, 0m, 0m,
            "At least five resolved candidates are required.");
        const int simulations = 2_000; var random = new Random(17);
        var totals = new decimal[simulations]; var drawdowns = new decimal[simulations];
        var losingRuns = 0;
        for (var simulation = 0; simulation < simulations; simulation++)
        {
            var equity = 0m; var peak = 0m; var maximumDrawdown = 0m; var run = 0; var maximumRun = 0;
            for (var trade = 0; trade < outcomes.Length; trade++)
            {
                var value = outcomes[random.Next(outcomes.Length)]; equity += value;
                peak = Math.Max(peak, equity); maximumDrawdown = Math.Max(maximumDrawdown, peak - equity);
                if (value < 0m) { run++; maximumRun = Math.Max(maximumRun, run); } else run = 0;
            }
            totals[simulation] = equity; drawdowns[simulation] = maximumDrawdown;
            if (maximumRun >= 5) losingRuns++;
        }
        Array.Sort(totals); Array.Sort(drawdowns);
        return new(simulations, outcomes.Length, Percentile(totals, .50m),
            Percentile(totals, .05m), Percentile(drawdowns, .95m),
            (decimal)losingRuns / simulations,
            "Deterministic bootstrap of resolved +1R/-1R outcomes; research estimate, not a forecast.");
    }

    private static List<OperationalAlertV5> Alerts(
        IReadOnlyList<DecisionFeatureSnapshotV2> current, ResearchIntelligenceV3 intelligence)
    {
        var result = new List<OperationalAlertV5>();
        if (intelligence.DataQuality.Status != "Healthy")
            result.Add(new("Critical", "DATA_QUALITY", $"Candle data is {intelligence.DataQuality.Status}."));
        if (current.Count == 0) result.Add(new("Critical", "NO_DECISIONS", "No decision snapshots were recorded."));
        if (current.Count > 0 && intelligence.EntryTiming.Candidates == 0)
            result.Add(new("Warning", "ZERO_CANDIDATES", "Decisions were recorded but no actionable candidate formed."));
        if (intelligence.EntryTiming.Candidates > 20)
            result.Add(new("Warning", "ABNORMAL_FREQUENCY", "More than twenty candidates formed in one session."));
        if (intelligence.Advanced?.Drift.Status == "DriftAlert")
            result.Add(new("Warning", "MODEL_DRIFT", "Volatility or regime distribution moved beyond the research baseline."));
        return result;
    }

    private static string BestTrial(IReadOnlyList<ParameterTrialV5> trials)
    {
        var best = trials.Where(value => value.Candidates > 0)
            .OrderByDescending(value => value.ExpectancyR).ThenByDescending(value => value.Candidates)
            .FirstOrDefault();
        return best is null ? "insufficient evidence" :
            $"confidence ≥{best.MinimumConfidence:P0}, maturity ≤{best.MaximumMaturityAtr:F0} ATR, {best.Candidates} candidates, {best.WinRate:P0} wins";
    }

    private static decimal Percentile(decimal[] ordered, decimal percentile)
    {
        if (ordered.Length == 0) return 0m;
        var index = (int)Math.Clamp(Math.Floor((ordered.Length - 1) * percentile), 0, ordered.Length - 1);
        return ordered[index];
    }
}
