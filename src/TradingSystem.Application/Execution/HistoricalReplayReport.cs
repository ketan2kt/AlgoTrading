namespace TradingSystem.Application.Execution;

public sealed record HistoricalReplaySessionCoverage(DateOnly Date, int IndexCandles,
    int OptionChainSnapshots, int OptionCandles, int RecordedCandidates, int BaselineTrades,
    decimal BaselineNetPnl, string Status, int ExpectedIndexMinutes,
    int MissingIndexMinutes, int LongestIndexGapMinutes);

public sealed record HistoricalReplayValidation(int TrainingDays, int ValidationDays,
    int TrainingTrades, int ValidationTrades, decimal TrainingNetPnl,
    decimal ValidationNetPnl, string Verdict);

public sealed record HistoricalReplayDayComparison(DateOnly Date, decimal ReferenceNetPnl,
    decimal ProposedNetPnl, decimal Difference, int ReferenceTrades, int ProposedTrades);

public sealed record HistoricalReplayResearchReport(DateOnly From, DateOnly To, string Market,
    string Variant, string DataFingerprint, HistoricalReplayParameters Parameters,
    IReadOnlyList<HistoricalReplaySessionCoverage> Sessions,
    HistoricalReplayResult Simulation, HistoricalReplayResult ReferenceSimulation,
    IReadOnlyList<HistoricalReplayDayComparison> DayComparisons,
    decimal BaselineNetPnl, HistoricalReplayValidation Validation,
    IReadOnlyList<string> Limitations);

public interface IHistoricalReplayReportReader
{
    Task<HistoricalReplayResearchReport> ReadAsync(DateOnly from, DateOnly endDate,
        string market, HistoricalReplayParameters parameters, CancellationToken cancellationToken);
}
