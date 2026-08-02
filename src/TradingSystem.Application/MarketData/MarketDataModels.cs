namespace TradingSystem.Application.MarketData;

public sealed record MarketObservation(Guid InstrumentId, string Source,
    DateTimeOffset SourceTimestampUtc, DateTimeOffset ReceivedAtUtc, decimal Price,
    long VolumeDelta, decimal? OpenInterest, long? Sequence = null);

public enum MarketDataRejectionReason
{
    None, Invalid, Stale, FutureTimestamp, Duplicate, OutOfOrder, SequenceGap
}

public sealed record MarketDataValidationResult(bool Accepted, bool TradingPermitted,
    MarketDataRejectionReason Reason, TimeSpan Age);

public sealed record CompletedCandle(Guid InstrumentId, string Source,
    DateTimeOffset OpenTimeUtc, int IntervalSeconds, decimal Open, decimal High,
    decimal Low, decimal Close, long Volume, decimal? OpenInterest);

public sealed record MarketDataHealthSnapshot(string Provider, bool Available,
    bool TradingPermitted, DateTimeOffset? LastObservationUtc,
    MarketDataRejectionReason? LastRejection, long AcceptedCount, long RejectedCount);

public interface IMarketDataHealthReader
{
    IReadOnlyList<MarketDataHealthSnapshot> GetCurrent();
}
