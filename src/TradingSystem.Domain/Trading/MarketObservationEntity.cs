using TradingSystem.Domain.Common;

namespace TradingSystem.Domain.Trading;

public sealed class PersistedMarketObservation : Entity, IAppendOnlyEntity
{
    public PersistedMarketObservation(
        Guid id,
        Guid instrumentId,
        string source,
        DateTimeOffset sourceTimestampUtc,
        DateTimeOffset receivedAtUtc,
        decimal price,
        long volumeDelta,
        decimal? openInterest) : base(id)
    {
        if (instrumentId == Guid.Empty || string.IsNullOrWhiteSpace(source) || price <= 0 || volumeDelta < 0)
        {
            throw new ArgumentException("Market observation values are invalid.");
        }

        InstrumentId = instrumentId;
        Source = source;
        SourceTimestampUtc = sourceTimestampUtc.ToUniversalTime();
        ReceivedAtUtc = receivedAtUtc.ToUniversalTime();
        Price = price;
        VolumeDelta = volumeDelta;
        OpenInterest = openInterest;
    }

    public Guid InstrumentId { get; private init; }
    public string Source { get; private init; }
    public DateTimeOffset SourceTimestampUtc { get; private init; }
    public DateTimeOffset ReceivedAtUtc { get; private init; }
    public decimal Price { get; private init; }
    public long VolumeDelta { get; private init; }
    public decimal? OpenInterest { get; private init; }
}
