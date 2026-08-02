using System.Collections.Concurrent;
using TradingSystem.Application.Broker;

namespace TradingSystem.Application.MarketData;

public sealed class GrowwQuoteNormalizer
{
    private readonly ConcurrentDictionary<Guid, decimal> cumulativeVolumes = new();

    public MarketObservation Normalize(
        Guid instrumentId,
        GrowwQuote quote,
        DateTimeOffset receivedAtUtc)
    {
        if (instrumentId == Guid.Empty) throw new ArgumentException("Instrument is required.", nameof(instrumentId));
        ArgumentNullException.ThrowIfNull(quote);
        var currentVolume = quote.Volume ?? 0;
        if (currentVolume < 0 || currentVolume > long.MaxValue || decimal.Truncate(currentVolume) != currentVolume)
            throw new ArgumentException("Groww cumulative volume is invalid.", nameof(quote));
        long delta = 0;
        cumulativeVolumes.AddOrUpdate(instrumentId, currentVolume, (_, previous) =>
        {
            if (currentVolume < previous)
                throw new InvalidOperationException("Groww cumulative volume regressed; session reconciliation is required.");
            delta = checked((long)(currentVolume - previous));
            return currentVolume;
        });

        return new MarketObservation(
            instrumentId,
            "Groww",
            DateTimeOffset.FromUnixTimeMilliseconds(quote.LastTradeTimeEpochMilliseconds),
            receivedAtUtc.ToUniversalTime(),
            quote.LastPrice,
            delta,
            quote.OpenInterest);
    }
}
