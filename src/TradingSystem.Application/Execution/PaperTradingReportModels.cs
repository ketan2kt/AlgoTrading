namespace TradingSystem.Application.Execution;

public sealed record PaperTradeHistoryItem(Guid SignalId, string Contract, string Direction,
    int Quantity, decimal EntryPrice, decimal ExitPrice, decimal RealisedPnl, string ExitReason,
    DateTimeOffset SignalTimeUtc, DateTimeOffset ExitTimeUtc, string Strategy,
    string Regime, int DaysToExpiry, PaperTradingCostBreakdown Costs,
    TradeExitQualityMetrics ExitQuality, string? ShadowStructureState,
    decimal? ShadowTrendQuality, bool? ShadowWouldPermit);

public sealed record DailyPaperPerformance(DateOnly Date, int Trades, int Wins, int Losses,
    decimal NetPnl, decimal GrossProfit, decimal GrossLoss, decimal WinRate,
    decimal MaximumDrawdown);

public sealed record StrategyPerformanceBreakdown(string Strategy, string Regime,
    string TimeBucket, int DaysToExpiry, int Trades, int Wins, decimal NetPnl,
    decimal WinRate, decimal AveragePnl, decimal Expectancy, decimal ProfitFactor);

public sealed record TradeExitQualityMetrics(decimal MaximumFavourableExcursion,
    decimal MaximumAdverseExcursion, decimal ProfitGiveback, decimal CapturedProfitRatio,
    decimal BestPostExitIncrementalPnl, int PriceSamples, string Assessment);

public sealed record StrategyDecisionFunnel(string Outcome, int Evaluations,
    decimal AverageConfidence, decimal AverageRelativeFuturesVolume,
    IReadOnlyList<DecisionReasonCount> LeadingReasons);

public sealed record DecisionReasonCount(string Reason, int Count);

public sealed record ResearchRecommendation(string Code, string Severity, string Message,
    int SupportingTrades, bool EligibleForExperiment);

public sealed record PaperResearchSummary(int ClosedTrades, decimal Expectancy,
    decimal ProfitFactor, decimal AverageMaximumFavourableExcursion,
    decimal AverageMaximumAdverseExcursion, decimal AverageProfitGiveback,
    int EarlyExitCandidates, int ProfitGivebackCandidates);

public sealed record ShadowStructurePerformance(string State, bool WouldPermit, int Trades,
    int Wins, decimal NetPnl, decimal Expectancy);

public sealed record PaperTradingReport(IReadOnlyList<DailyPaperPerformance> Daily,
    IReadOnlyList<PaperTradeHistoryItem> Trades,
    IReadOnlyList<StrategyPerformanceBreakdown> Breakdown,
    IReadOnlyList<StrategyDecisionFunnel> DecisionFunnel,
    IReadOnlyList<ResearchRecommendation> Recommendations,
    PaperResearchSummary Research,
    IReadOnlyList<ShadowStructurePerformance> ShadowStructure,
    PaperStrategyReplayReport Replay,
    DateTimeOffset ObservedAtUtc);

public interface IPaperTradingReportReader
{
    Task<PaperTradingReport> GetAsync(int days, CancellationToken cancellationToken);

    Task<PaperPnlSummary> GetPnlSummaryAsync(DateOnly from, DateOnly to, string market,
        CancellationToken cancellationToken);
}

public sealed record PaperPnlObservation(string Market, decimal NetPnl, decimal Charges);

public sealed record PaperMarketPnl(string Market, int Trades, int Wins, int Losses,
    decimal GrossProfit, decimal GrossLoss, decimal Charges, decimal NetPnl);

public sealed record PaperPnlSummary(DateOnly From, DateOnly To, string Market,
    IReadOnlyList<PaperMarketPnl> Markets, int TotalTrades, decimal TotalCharges,
    decimal TotalNetPnl, DateTimeOffset ObservedAtUtc);

public static class PaperPnlSummaryCalculator
{
    private static readonly string[] MarketOrder = ["nifty", "sensex", "natural-gas"];

    public static IReadOnlyList<PaperMarketPnl> Summarise(IEnumerable<PaperPnlObservation> observations) =>
        observations.GroupBy(value => value.Market, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => Array.IndexOf(MarketOrder, group.Key.ToLowerInvariant()))
            .Select(group => new PaperMarketPnl(group.Key.ToLowerInvariant(), group.Count(),
                group.Count(value => value.NetPnl > 0), group.Count(value => value.NetPnl < 0),
                group.Where(value => value.NetPnl > 0).Sum(value => value.NetPnl),
                group.Where(value => value.NetPnl < 0).Sum(value => value.NetPnl),
                group.Sum(value => value.Charges), group.Sum(value => value.NetPnl)))
            .ToArray();
}
