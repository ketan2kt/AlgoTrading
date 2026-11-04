namespace TradingSystem.Application.Diagnostics;

public static class DailyAnalysisAvailabilityPolicy
{
    public static TimeOnly AvailableAt { get; } = new(15, 15);

    public static DateTimeOffset AvailableAtUtc(DateOnly tradingDate) =>
        new DateTimeOffset(tradingDate.ToDateTime(AvailableAt), TimeSpan.FromHours(5.5))
            .ToUniversalTime();

    public static bool IsAvailable(DateOnly requestedDate, DateOnly today, DateTimeOffset nowUtc) =>
        requestedDate < today || nowUtc >= AvailableAtUtc(today);
}

public interface IDailyTradingDiagnosticsReader
{
    Task<DailyTradingDiagnosticsReport> ReadWithTimelineAsync(DateOnly tradingDate, CancellationToken token)
        => ReadAsync(tradingDate, "all", token);
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
    IReadOnlyList<string> TelemetryWarnings)
{
    public IReadOnlyList<ExpansionPairObservation> ExpansionPairObservations { get; init; } = [];
    public IReadOnlyList<EngineEvidenceCoverage> EngineEvidenceCoverage { get; init; } = [];
    public IReadOnlyList<DailyExecutionReview> ExecutionReview { get; init; } = [];
    public DailyLivePnlSummary? LivePnl { get; init; }
    public PaperSessionTimeline? Timeline { get; init; }
}

public sealed record PaperSessionTimeline(IReadOnlyList<PaperTimelineTrade> Trades, string? UnavailableReason = null, string? CoverageNote = null);
public sealed record PaperTimelineTrade(Guid Id, DateTimeOffset From, DateTimeOffset To, decimal FinalGross,
    int Sign, bool Verified, IReadOnlyList<PaperTimelineEvent> Events, IReadOnlyList<PaperTimelineMark> Marks);
public sealed record PaperTimelineEvent(DateTimeOffset At, string Kind, int Units, decimal Price);
public sealed record PaperTimelineMark(DateTimeOffset At, decimal Price, DateTimeOffset? SourceAt);

public sealed record DailyExecutionReview(Guid TradeId, string Market, string Symbol,
    string IntentStatus, string Finding, DateTimeOffset? ObservedAtUtc,
    double? PaperToIntentSeconds, double? IntentToSubmittedSeconds)
{
    public string? EntryRoute { get; init; }
    public IReadOnlyList<StopComparisonRow> StopHistory { get; init; } = [];
    public LivePnlComparison? Pnl { get; init; }
}

public sealed record LivePnlComparison(string Status, string Reason, int? ClosedUnits = null,
    decimal? LiveGross = null, decimal? EstimatedCharges = null, decimal? MatchedPaperGross = null)
{
    public decimal? EstimatedNet => LiveGross - EstimatedCharges;
    public decimal? ExecutionDifference => LiveGross - MatchedPaperGross;
    public decimal? EntryDifference { get; init; }
    public decimal? ExitDifference { get; init; }
}
public sealed record DailyLivePnlSummary(int Confirmed, int Comparable, int PaperOnly, int NotFilled,
    int Open, int Incomplete, decimal? ConfirmedGrossSubtotal, decimal? EstimatedNetSubtotal,
    decimal? MatchedPaperGrossSubtotal, decimal? MatchedExecutionDifference);

public sealed record StopComparisonRow(DateTimeOffset? PaperAt, decimal? PaperStop, int? PaperQuantity,
    DateTimeOffset? ConfirmedAt, decimal? BrokerStop, int? BrokerQuantity, string? OrderId,
    double? SecondsToConfirmation, DateTimeOffset? FillObservedAt, decimal? FillPrice, int? FillQuantity, string Finding)
{
    public double? SecondsBeforeCall { get; init; }
    public double? SecondsInCallAndVerification { get; init; }
}

public sealed record EngineEvidenceCoverage(string EngineKey, int Records,
    DateTimeOffset FirstAtUtc, DateTimeOffset LastAtUtc);

public sealed record EngineEvidencePage(DateOnly TradingDate, string Market,
    int Offset, int? NextOffset, IReadOnlyList<DailyTradingSystemEvent> Records);

public interface IEngineEvidenceReader
{
    Task<EngineEvidencePage> ReadAsync(DateOnly tradingDate, string market, int offset,
        int limit, CancellationToken cancellationToken);
}

public sealed record ExpansionPairObservation(
    string Market,
    DateTimeOffset ObservedAtUtc,
    string EvidenceJson);

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
