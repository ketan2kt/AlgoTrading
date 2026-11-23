namespace TradingSystem.Application.Broker;

/// <summary>One execution account. Market-data providers and paper execution are deliberately separate.</summary>
public interface ILiveExecutionBrokerGateway : IBrokerGateway, ILiveBrokerProtectionGateway, ILiveAccountEvidenceGateway
{
    string Broker { get; }
    string Account { get; }
    decimal ProtectionLimit(decimal stopLoss);
    new Task CancelProtectionAsync(string brokerProtectionId, CancellationToken cancellationToken);
    new Task<BrokerProtectionSnapshot> ModifyProtectionAsync(string brokerProtectionId, int quantity,
        decimal stopLoss, CancellationToken cancellationToken);
    Task<BrokerProtectionSnapshot> ConfirmProtectionAsync(string id, int quantity, decimal stopLoss,
        CancellationToken cancellationToken);
}
