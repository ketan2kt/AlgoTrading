namespace TradingSystem.Application.Broker;

/// <summary>Test and replay control surface; it is never exposed by a live gateway.</summary>
public interface IPaperBrokerControl
{
    Task<BrokerOrderSnapshot> ProcessNextFillAsync(
        string clientReference,
        CancellationToken cancellationToken);
}
