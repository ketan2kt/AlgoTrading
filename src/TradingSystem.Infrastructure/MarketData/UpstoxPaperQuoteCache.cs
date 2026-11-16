using System.Collections.Concurrent;
using TradingSystem.Application.Broker;
using TradingSystem.Application.MarketData;

namespace TradingSystem.Infrastructure.MarketData;

internal sealed class UpstoxPaperQuoteCache
{
    private readonly ConcurrentDictionary<string, GrowwQuote> quotes = new(StringComparer.Ordinal);
    public void Observe(string key, GrowwQuote quote, DateTimeOffset at) => quotes.AddOrUpdate(key,
        quote with { Source = "Upstox", ObservedAtUtc = at }, (_, old) => old.ObservedAtUtc >= at ? old :
            quote with { Source = "Upstox", ObservedAtUtc = at });
    public bool TryGet(string key, DateTimeOffset now, out GrowwQuote quote)
    {
        if (quotes.TryGetValue(key, out quote!) && quote.ObservedAtUtc is { } at && at <= now &&
            now - at <= TimeSpan.FromSeconds(4) &&
            (GrowwQuoteTimestamp.ToUtc(quote.LastTradeTimeEpochMilliseconds) is not { } trade ||
             trade <= now && now - trade <= TimeSpan.FromSeconds(30))) return true;
        quote = null!;
        return false;
    }
}
