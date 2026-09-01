namespace TradingSystem.Application.Broker;

public interface ILiveBrokerProtectionGateway
{
    Task<BrokerProtectionSnapshot> CreateProtectionAsync(BrokerProtectionRequest request,
        CancellationToken cancellationToken);
    Task<BrokerProtectionSnapshot> ModifyProtectionAsync(string brokerProtectionId,
        int quantity, decimal target, decimal stopLoss, CancellationToken cancellationToken);
    Task CancelProtectionAsync(string brokerProtectionId, CancellationToken cancellationToken);
}

public sealed record BrokerProtectionRequest(
    string ClientReference,
    string TradingSymbol,
    string Exchange,
    int Quantity,
    int NetPositionQuantity,
    decimal Target,
    decimal StopLoss);

public sealed record BrokerProtectionSnapshot(
    string ClientReference,
    string BrokerProtectionId,
    string Status,
    int Quantity,
    decimal Target,
    decimal StopLoss,
    DateTimeOffset UpdatedAtUtc);
