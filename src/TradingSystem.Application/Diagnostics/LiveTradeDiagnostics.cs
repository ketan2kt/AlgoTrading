namespace TradingSystem.Application.Diagnostics;

public interface ILiveTradeDiagnosticsReader
{
    Task<LiveTradeDiagnosticsReport?> ReadAsync(Guid paperTradeId, CancellationToken cancellationToken);
}

public sealed record LiveTradeDiagnosticsReport(
    Guid PaperTradeId,
    Guid SourceId,
    string Market,
    string? PaperStrategy,
    string? PaperStatus,
    string IntentStatus,
    DateTimeOffset? IntentCreatedAtUtc,
    DateTimeOffset? EntrySubmittedAtUtc,
    DateTimeOffset? ProtectionPlacedAtUtc,
    decimal PaperEntryPrice,
    decimal? CurrentPaperStopPrice,
    decimal? PaperTargetPrice,
    decimal? LiveEntryFillPrice,
    IReadOnlyList<LiveTradeTimelineEvent> Timeline,
    IReadOnlyList<LiveBrokerOrderEvidence> BrokerOrders,
    IReadOnlyList<string> Limitations);

public sealed record LiveTradeTimelineEvent(DateTimeOffset ObservedAtUtc, string Event,
    string EvidenceJson);

public sealed record LiveBrokerOrderEvidence(string Role, string ClientReference,
    string? BrokerOrderId, string? OrderStatus, string? OrderType,
    decimal? LimitPrice, decimal? TriggerPrice, int? FilledQuantity,
    decimal? AverageFillPrice, string? BrokerCreatedAt, string? ExchangeTime,
    DateTimeOffset ObservedAtUtc, IReadOnlyList<LiveBrokerFillEvidence> Fills);

public sealed record LiveBrokerFillEvidence(int Quantity, decimal Price,
    string? TradeDateTime);
