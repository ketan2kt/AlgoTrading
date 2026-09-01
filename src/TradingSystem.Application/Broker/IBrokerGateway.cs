using TradingSystem.Domain;
using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Broker;

public interface IBrokerGateway
{
    TradingMode Mode { get; }

    Task<BrokerOrderSnapshot> SubmitAsync(
        BrokerOrderRequest request,
        CancellationToken cancellationToken);

    Task<BrokerOrderSnapshot?> GetOrderAsync(
        string clientReference,
        CancellationToken cancellationToken);

    Task<BrokerOrderSnapshot> CancelAsync(
        string clientReference,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<BrokerPositionSnapshot>> GetPositionsAsync(
        CancellationToken cancellationToken);

    Task<BrokerReconciliationResult> ReconcileAsync(
        IReadOnlyCollection<ExpectedBrokerPosition> expectedPositions,
        CancellationToken cancellationToken);
}

public sealed record BrokerOrderRequest(
    string ClientReference,
    Guid InstrumentId,
    Direction Direction,
    int Quantity,
    decimal ExecutionPrice,
    int MaximumFillQuantityPerCycle = int.MaxValue,
    string? TradingSymbol = null,
    string? Exchange = null,
    string Segment = "FNO",
    string Product = "MIS",
    string OrderType = "MARKET",
    decimal? TriggerPrice = null);

public sealed record BrokerOrderSnapshot(
    string ClientReference,
    string BrokerOrderId,
    Guid InstrumentId,
    Direction Direction,
    int RequestedQuantity,
    int FilledQuantity,
    decimal? AverageFillPrice,
    OrderState State,
    DateTimeOffset UpdatedAtUtc);

public sealed record BrokerPositionSnapshot(
    Guid InstrumentId,
    Direction Direction,
    int Quantity,
    decimal AverageEntryPrice);

public sealed record ExpectedBrokerPosition(
    Guid InstrumentId,
    Direction Direction,
    int Quantity);

public sealed record BrokerReconciliationResult(
    bool IsMatched,
    bool TradingPermitted,
    IReadOnlyList<string> Mismatches,
    DateTimeOffset CheckedAtUtc);

public sealed class DuplicateBrokerOrderException(string message) : InvalidOperationException(message);

public sealed class BrokerOrderNotFoundException(string clientReference)
    : InvalidOperationException($"Broker order '{clientReference}' was not found.");

public sealed class BrokerOrderOutcomeUnknownException(string clientReference, Exception? innerException = null)
    : InvalidOperationException(
        $"Broker order '{clientReference}' has an unknown submission outcome. Reconciliation is required before another submission.",
        innerException);
