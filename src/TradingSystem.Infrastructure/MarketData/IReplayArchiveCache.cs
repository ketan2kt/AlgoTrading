namespace TradingSystem.Infrastructure.MarketData;

public interface IReplayArchiveCache
{
    bool Enabled { get; }
    string CacheRoot { get; }
    Task HydrateAsync(string category, DateOnly? tradingDate, CancellationToken token);
}
