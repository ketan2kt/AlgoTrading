namespace TradingSystem.Application.Execution;

public sealed record ResearchTradeObservation(DateOnly SessionDate, string Strategy,
    string TimeBucket, decimal NetPnl, decimal Charges);

public sealed record ResearchDecisionObservation(DateOnly SessionDate, string Outcome);

public sealed record ResearchEvidence(string Code, string Assessment, string Message,
    int SupportingTrades, decimal NetPnl);

public sealed record ResearchChallenger(string Code, string Status, string Hypothesis,
    string ValidationRule);

public sealed record SelfImprovementResearchReport(string Version, string Market,
    DateOnly SessionDate, int EvidenceSessions, decimal TargetWinRate,
    decimal ActualWinRate, bool TargetMet, int Trades, int Wins, decimal NetPnl, decimal Charges,
    decimal ProfitFactor, int AcceptedDecisions, int RejectedDecisions,
    IReadOnlyList<ResearchEvidence> Evidence,
    IReadOnlyList<ResearchChallenger> Challengers, string Verdict,
    DateTimeOffset GeneratedAtUtc);

public interface ISelfImprovementResearchReader
{
    Task<IReadOnlyList<SelfImprovementResearchReport>> GetLatestAsync(
        CancellationToken cancellationToken);
}

public static class SelfImprovementResearchAnalyzer
{
    public const int RollingSessionLimit = 20;
    public const int MinimumEvidenceSessions = 5;
    public const int MinimumEvidenceTrades = 30;
    public const decimal TargetWinRate = 70m;

    public static SelfImprovementResearchReport Analyze(string market, DateOnly sessionDate,
        IEnumerable<ResearchTradeObservation> tradeSource,
        IEnumerable<ResearchDecisionObservation> decisionSource, DateTimeOffset generatedAtUtc)
    {
        var allTrades = tradeSource.Where(value => value.SessionDate <= sessionDate).ToArray();
        var sessionDates = allTrades.Select(value => value.SessionDate).Distinct()
            .OrderDescending().Take(RollingSessionLimit).Order().ToArray();
        var dates = sessionDates.ToHashSet();
        var trades = allTrades.Where(value => dates.Contains(value.SessionDate)).ToArray();
        var decisions = decisionSource.Where(value => dates.Contains(value.SessionDate)).ToArray();
        var wins = trades.Count(value => value.NetPnl > 0);
        var winRate = trades.Length == 0 ? 0m : wins * 100m / trades.Length;
        var grossProfit = trades.Where(value => value.NetPnl > 0).Sum(value => value.NetPnl);
        var grossLoss = Math.Abs(trades.Where(value => value.NetPnl < 0).Sum(value => value.NetPnl));
        var profitFactor = grossLoss == 0 ? (grossProfit > 0 ? 99m : 0m) : grossProfit / grossLoss;
        var evidence = new List<ResearchEvidence>();
        var challengers = new List<ResearchChallenger>();

        evidence.Add(new("ROLLING_TRADE_PRECISION", winRate >= TargetWinRate ? "LockHarder" : "Investigate",
            $"{wins} of {trades.Length} trades were profitable ({winRate:0.0}%); target is at least {TargetWinRate:0}% over repeated multi-session evidence.",
            trades.Length, trades.Sum(value => value.NetPnl)));

        foreach (var segment in trades.GroupBy(value => $"{value.Strategy}|{value.TimeBucket}"))
        {
            var net = segment.Sum(value => value.NetPnl);
            var segmentSessions = segment.Select(value => value.SessionDate).Distinct().Count();
            var segmentWinRate = segment.Count(value => value.NetPnl > 0) * 100m / segment.Count();
            if (segment.Count() < 10 || segmentSessions < 3 || net >= 0 || segmentWinRate >= 50m) continue;
            evidence.Add(new("WEAK_SEGMENT", "Investigate",
                $"{segment.Key} lost {net:0.00} across {segment.Count()} trades.", segment.Count(), net));
            challengers.Add(new($"SHADOW_{Sanitize(segment.Key)}", "ShadowOnly",
                $"Raise confirmation or suppress repeat entries only inside {segment.Key}.",
                "Replay first, then require at least 30 out-of-sample shadow trades across 5 sessions, >=70% wins, positive expectancy, profit factor >=1.20, and no worse drawdown before promotion review."));
        }

        foreach (var segment in trades.GroupBy(value => $"{value.Strategy}|{value.TimeBucket}"))
        {
            var segmentSessions = segment.Select(value => value.SessionDate).Distinct().Count();
            var segmentWinRate = segment.Count(value => value.NetPnl > 0) * 100m / segment.Count();
            var net = segment.Sum(value => value.NetPnl);
            if (segment.Count() < MinimumEvidenceTrades || segmentSessions < MinimumEvidenceSessions ||
                segmentWinRate < TargetWinRate || net <= 0) continue;
            evidence.Add(new("PROVEN_EDGE", "LockHarder",
                $"{segment.Key} repeatedly produced {segmentWinRate:0.0}% profitable trades and {net:0.00} net P&L across {segment.Count()} trades and {segmentSessions} sessions.",
                segment.Count(), net));
        }

        var averageTrades = sessionDates.Length == 0 ? 0m : (decimal)trades.Length / sessionDates.Length;
        if (averageTrades > 6)
        {
            evidence.Add(new("TRADE_FREQUENCY", "Investigate",
                $"Average frequency is {averageTrades:0.0} trades per completed session.", trades.Length,
                trades.Sum(value => value.NetPnl)));
            challengers.Add(new("SHADOW_FREQUENCY_GUARD", "ShadowOnly",
                "Test a cooldown after failed setups and require fresh structural evidence for re-entry.",
                "Promote only after out-of-sample shadow evidence improves net expectancy and drawdown over at least 10 sessions."));
        }

        var charges = trades.Sum(value => value.Charges);
        var preCost = trades.Sum(value => value.NetPnl) + charges;
        if (charges > 0 && (preCost <= 0 || charges >= Math.Abs(preCost) * 0.25m))
            evidence.Add(new("COST_DRAG", "Investigate",
                $"Estimated charges were {charges:0.00} against pre-cost P&L of {preCost:0.00}.",
                trades.Length, trades.Sum(value => value.NetPnl)));

        var accepted = decisions.Count(value => IsAccepted(value.Outcome));
        var rejected = Math.Max(0, decisions.Length - accepted);
        var sufficientEvidence = sessionDates.Length >= MinimumEvidenceSessions &&
                                 trades.Length >= MinimumEvidenceTrades;
        var targetMet = sufficientEvidence && winRate >= TargetWinRate && profitFactor >= 1.2m;
        var verdict = !sufficientEvidence
            ? "CollectEvidence"
            : targetMet ? "TargetMetLockProvenEdges"
            : "ResearchRequiredKeepChampion";

        return new("self-improvement-v1", market, sessionDate, sessionDates.Length,
            TargetWinRate, decimal.Round(winRate, 2), targetMet, trades.Length,
            wins, trades.Sum(value => value.NetPnl), charges,
            decimal.Round(profitFactor, 3), accepted, rejected, evidence, challengers, verdict,
            generatedAtUtc.ToUniversalTime());
    }

    private static bool IsAccepted(string outcome) =>
        outcome.Contains("Opened", StringComparison.OrdinalIgnoreCase) ||
        outcome.Contains("Accepted", StringComparison.OrdinalIgnoreCase) ||
        outcome.Contains("Signal", StringComparison.OrdinalIgnoreCase) &&
        !outcome.Contains("NoSignal", StringComparison.OrdinalIgnoreCase);

    private static string Sanitize(string value) => new(value.ToUpperInvariant()
        .Select(character => char.IsLetterOrDigit(character) ? character : '_').ToArray());
}
