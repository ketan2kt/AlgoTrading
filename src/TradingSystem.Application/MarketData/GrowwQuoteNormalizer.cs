using System.Collections.Concurrent;
using TradingSystem.Application.Broker;

namespace TradingSystem.Application.MarketData;

public sealed class GrowwQuoteNormalizer
{
    private static readonly TimeSpan IndiaOffset = TimeSpan.FromMinutes(330);
    private readonly ConcurrentDictionary<Guid, VolumeState> cumulativeVolumes = new();

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
        var sessionDate = DateOnly.FromDateTime(receivedAtUtc.ToOffset(IndiaOffset).DateTime);
        long delta = 0;
        cumulativeVolumes.AddOrUpdate(instrumentId,
            _ => new VolumeState(sessionDate, currentVolume),
            (_, previous) =>
        {
            if (sessionDate > previous.SessionDate)
                return new VolumeState(sessionDate, currentVolume);
            if (sessionDate < previous.SessionDate)
                throw new InvalidOperationException("Groww cumulative volume belongs to an older trading session.");
            if (currentVolume < previous.CumulativeVolume)
                throw new InvalidOperationException("Groww cumulative volume regressed; session reconciliation is required.");
            delta = checked((long)(currentVolume - previous.CumulativeVolume));
            return new VolumeState(sessionDate, currentVolume);
        });

        return new MarketObservation(
            instrumentId,
            "Groww",
            quote.LastTradeTimeEpochMilliseconds is > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(quote.LastTradeTimeEpochMilliseconds.Value)
                : receivedAtUtc.ToUniversalTime(),
            receivedAtUtc.ToUniversalTime(),
            quote.LastPrice,
            delta,
            quote.OpenInterest);
    }

    private sealed record VolumeState(DateOnly SessionDate, decimal CumulativeVolume);
}
