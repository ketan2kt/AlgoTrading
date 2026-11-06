using TradingSystem.Application.MarketData;

namespace TradingSystem.Infrastructure.MarketData;

internal static class FuturesFeedPollingCycle
{
    internal static async Task RunAsync(Func<TradingMarketDefinition, Task> process,
        Action<Exception> failed, CancellationToken cancellationToken)
    {
        foreach (var market in new[] { TradingMarketCatalog.Sensex, TradingMarketCatalog.BankNifty })
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { await process(market); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) { failed(exception); }
        }
    }
}
