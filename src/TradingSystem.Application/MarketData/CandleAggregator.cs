using System.Collections.Concurrent;

namespace TradingSystem.Application.MarketData;

public sealed class CandleAggregator(int intervalSeconds)
{
    private readonly ConcurrentDictionary<(Guid, string), BuildingCandle> candles = new();

    public CompletedCandle? Add(MarketObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(intervalSeconds);
        var unix = observation.SourceTimestampUtc.ToUnixTimeSeconds();
        var bucket = DateTimeOffset.FromUnixTimeSeconds(unix - unix % intervalSeconds);
        var key = (observation.InstrumentId, observation.Source);
        CompletedCandle? completed = null;
        candles.AddOrUpdate(key, _ => BuildingCandle.Start(observation, bucket), (_, current) =>
        {
            if (bucket < current.OpenTimeUtc) throw new InvalidOperationException("Out-of-order observation.");
            if (bucket > current.OpenTimeUtc)
            {
                completed = current.Complete(intervalSeconds);
                return BuildingCandle.Start(observation, bucket);
            }
            current.Apply(observation);
            return current;
        });
        return completed;
    }

    private sealed class BuildingCandle
    {
        public required Guid InstrumentId { get; init; }
        public required string Source { get; init; }
        public required DateTimeOffset OpenTimeUtc { get; init; }
        public decimal Open { get; init; }
        public decimal High { get; private set; }
        public decimal Low { get; private set; }
        public decimal Close { get; private set; }
        public long Volume { get; private set; }
        public decimal? OpenInterest { get; private set; }
        public static BuildingCandle Start(MarketObservation value, DateTimeOffset bucket) => new()
        {
            InstrumentId = value.InstrumentId,
            Source = value.Source,
            OpenTimeUtc = bucket,
            Open = value.Price,
            High = value.Price,
            Low = value.Price,
            Close = value.Price,
            Volume = value.VolumeDelta,
            OpenInterest = value.OpenInterest
        };
        public void Apply(MarketObservation value)
        {
            High = Math.Max(High, value.Price); Low = Math.Min(Low, value.Price); Close = value.Price;
            Volume = checked(Volume + value.VolumeDelta); OpenInterest = value.OpenInterest ?? OpenInterest;
        }
        public CompletedCandle Complete(int seconds) => new(InstrumentId, Source, OpenTimeUtc,
            seconds, Open, High, Low, Close, Volume, OpenInterest);
    }
}
