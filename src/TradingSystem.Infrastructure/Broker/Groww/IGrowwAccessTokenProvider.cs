namespace TradingSystem.Infrastructure.Broker.Groww;

public interface IGrowwAccessTokenProvider
{
    ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken);
}
