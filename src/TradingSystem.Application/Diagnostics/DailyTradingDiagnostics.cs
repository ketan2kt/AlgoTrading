namespace TradingSystem.Application.Diagnostics;

public interface IDailyTradingDiagnosticsReader
{
    Task<DailyTradingDiagnosticsReport> ReadAsync(
        DateOnly tradingDate,
        string market,
        CancellationToken cancellationToken);
}

public sealed record DailyTradingDiagnosticsReport(
    DateOnly TradingDate,
    string Market,
    DateTimeOffset GeneratedAtUtc,
    DailyTradingDiagnosticsSummary Summary,
    IReadOnlyList<DailyTradingFeedStatus> Feeds,
    IReadOnlyList<DailyTradingDecision> Decisions,
    IReadOnlyList<DailyTradingTrade> Trades,
    IReadOnlyList<DailyTradingSystemEvent> SystemEvents,
    IReadOnlyList<string> TelemetryWarnings);

public sealed record DailyTradingDiagnosticsSummary(
    int DecisionCount,
    int AcceptedDecisionCount,
    int RejectedDecisionCount,
    IReadOnlyDictionary<string, int> Outcomes,
    int CompletedTradeCount,
    int OpenTradeCount,
    int Wins,
    int Losses,
    decimal GrossPnl,
    decimal Costs,
    decimal NetRealisedPnl,
    DateTimeOffset? FirstDecisionAtUtc,
    DateTimeOffset? LastDecisionAtUtc,
    DateTimeOffset? LastCandleAtUtc);

public sealed record DailyTradingFeedStatus(
    string Provider,
    bool IsAvailable,
    DateTimeOffset? LastSuccessAtUtc,
    DateTimeOffset? LastFailureAtUtc,
    string? ErrorCode,
    DateTimeOffset UpdatedAtUtc);

public sealed record DailyTradingDecision(
    string Market,
    string Source,
    DateTimeOffset CandleTimeUtc,
    string Outcome,
    decimal Confidence,
    string? Regime,
    string? Bias,
    decimal? Price,
    string EvidenceJson,
    Guid? SignalId);

public sealed record DailyTradingTrade(
    string Market,
    string Source,
    Guid TradeId,
    Guid? SignalId,
    string Symbol,
    string Strategy,
    string Direction,
    int Quantity,
    decimal EntryPrice,
    decimal ExitOrCurrentPrice,
    DateTimeOffset OpenedAtUtc,
    DateTimeOffset? ClosedAtUtc,
    string Status,
    string? ExitReason,
    decimal GrossPnl,
    decimal Costs,
    decimal NetRealisedPnl,
    decimal? MaximumFavourableExcursion,
    DateTimeOffset? MaximumFavourableExcursionAtUtc,
    decimal? MaximumAdverseExcursion,
    DateTimeOffset? MaximumAdverseExcursionAtUtc,
    string ExcursionCoverage = "Unknown",
    int ExcursionSampleCount = 0);

public sealed record DailyTradingSystemEvent(
    DateTimeOffset OccurredAtUtc,
    string Severity,
    string EventType,
    string Message,
    string? DetailsJson);
