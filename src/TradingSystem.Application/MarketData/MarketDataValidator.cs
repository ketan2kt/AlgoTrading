using System.Collections.Concurrent;

namespace TradingSystem.Application.MarketData;

public sealed class MarketDataValidator(TimeProvider timeProvider, TimeSpan maximumAge)
{
    private readonly ConcurrentDictionary<(Guid, string), Cursor> cursors = new();

    public MarketDataValidationResult Validate(MarketObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var age = timeProvider.GetUtcNow() - observation.SourceTimestampUtc;
        if (observation.InstrumentId == Guid.Empty || string.IsNullOrWhiteSpace(observation.Source) ||
            observation.Price <= 0 || observation.VolumeDelta < 0)
            return Reject(MarketDataRejectionReason.Invalid, age);
        if (age > maximumAge) return Reject(MarketDataRejectionReason.Stale, age);
        if (age < TimeSpan.FromSeconds(-1)) return Reject(MarketDataRejectionReason.FutureTimestamp, age);

        var key = (observation.InstrumentId, observation.Source);
        while (true)
        {
            if (!cursors.TryGetValue(key, out var cursor))
            {
                if (cursors.TryAdd(key, new(observation.SourceTimestampUtc, observation.Sequence)))
                    return Accept(age);
                continue;
            }
            if (observation.SourceTimestampUtc == cursor.Timestamp && observation.Sequence == cursor.Sequence)
                return Reject(MarketDataRejectionReason.Duplicate, age);
            if (observation.SourceTimestampUtc < cursor.Timestamp ||
                observation.Sequence is not null && cursor.Sequence is not null && observation.Sequence <= cursor.Sequence)
                return Reject(MarketDataRejectionReason.OutOfOrder, age);

            if (!cursors.TryUpdate(key, new(observation.SourceTimestampUtc, observation.Sequence), cursor))
                continue;
            if (observation.Sequence is not null && cursor.Sequence is not null && observation.Sequence > cursor.Sequence + 1)
                return Reject(MarketDataRejectionReason.SequenceGap, age);
            return Accept(age);
        }
    }

    private static MarketDataValidationResult Accept(TimeSpan age) => new(true, true, MarketDataRejectionReason.None, age);
    private static MarketDataValidationResult Reject(MarketDataRejectionReason reason, TimeSpan age) => new(false, false, reason, age);
    private sealed record Cursor(DateTimeOffset Timestamp, long? Sequence);
}
