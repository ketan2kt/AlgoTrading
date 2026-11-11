namespace TradingSystem.Infrastructure.MarketData;

public interface IReplayArchiveCache
{
    bool Enabled { get; }
    string CacheRoot { get; }
    IDisposable? Pin(DateOnly tradingDate) => null;
    Task HydrateAsync(string category, DateOnly? tradingDate, CancellationToken token);
    Task HydrateStreamingQuotesAsync(DateOnly tradingDate, CancellationToken token) => HydrateAsync("quotes", tradingDate, token);
}
