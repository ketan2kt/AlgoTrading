using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Broker;

public sealed record LiveAccountOrder(string Id, string Reference, string Symbol, string Exchange,
    string Product, Direction Direction, int Quantity, int Filled, OrderState State,
    DateTimeOffset CreatedAt, decimal? Trigger = null);
public sealed record LiveAccountPosition(string Symbol, string Exchange, string Product, int Quantity);
public sealed record LiveAccountSnapshot(IReadOnlyList<LiveAccountOrder> Orders,
    IReadOnlyList<LiveAccountPosition> Positions, DateTimeOffset ObservedAt);
public sealed record ExternalExitFill(string OrderId, int Quantity, decimal AveragePrice,
    DateTimeOffset ObservedAt);

public interface ILiveAccountEvidenceGateway
{
    Task<LiveAccountSnapshot> ReadAccountAsync(CancellationToken token);
    Task<ExternalExitFill> ReadExternalFillAsync(string orderId, int expectedQuantity, CancellationToken token);
    Task CancelProtectionAsync(string brokerProtectionId, CancellationToken token);
    Task<BrokerProtectionSnapshot> ModifyProtectionAsync(string brokerProtectionId, int quantity,
        decimal stopLoss, CancellationToken token);
}
