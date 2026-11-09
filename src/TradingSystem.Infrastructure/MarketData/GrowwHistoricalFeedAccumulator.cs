using TradingSystem.Application.Broker;
using TradingSystem.Application.Execution;

namespace TradingSystem.Infrastructure.MarketData;

internal sealed class GrowwHistoricalFeedAccumulator(IHistoricalQuoteArchive archive)
{
    private readonly Dictionary<string, State> states = new(StringComparer.Ordinal);
    private readonly object gate = new();

    public void Invalidate(HistoricalFeedInstrument instrument, string kind, DateTimeOffset received)
    {
        lock (gate)
        {
            var key = instrument.Exchange + ":" + instrument.Segment + ":" + instrument.ExchangeToken;
            if (!states.TryGetValue(key, out var state)) return;
            if (kind == "depth") { state.Depth = null; state.DepthReceived = received; }
            else { state.Price = null; state.PriceReceived = received; }
        }
    }

    public void Observe(HistoricalFeedInstrument instrument, string kind, GrowwFeedPoint point, DateTimeOffset received)
    {
        lock (gate)
        {
            var key = instrument.Exchange + ":" + instrument.Segment + ":" + instrument.ExchangeToken;
            if (!states.TryGetValue(key, out var state)) states.Add(key, state = new State(instrument));
            if (kind == "depth")
            {
                if (received < state.DepthReceived || point.SourceAtUtc < state.Depth?.SourceAtUtc) return;
                state.Depth = point;
                state.DepthReceived = received;
            }
            else
            {
                if (received < state.PriceReceived || point.SourceAtUtc < state.Price?.SourceAtUtc) return;
                state.Price = point;
                state.PriceReceived = received;
            }
        }
    }

    // One receipt-time snapshot for the whole universe: avoids creating a
    // separate replay clock event for every instrument update. Original price
    // and depth times remain attached; this never retimes a future tick.
    public void Sample(DateTimeOffset received)
    {
        lock (gate)
        foreach (var state in states.Values)
        {
            var instrument = state.Instrument;
            if (state.Price?.Last is not > 0m || state.PriceReceived > received ||
                instrument.Type != TradingSystem.Domain.Trading.InstrumentType.Index &&
                    (state.Depth is null || state.DepthReceived > received) ||
                received <= state.LastRecorded) continue;
            var price = state.Price;
            var depth = state.Depth;
            archive.TryRecord(new(Guid.NewGuid(), received,
                new(instrument.Exchange, instrument.Segment, instrument.TradingSymbol),
                new(price.Last.Value, price.SourceAtUtc.ToUnixTimeMilliseconds(), depth?.Bid,
                    depth?.BidQuantity, depth?.Offer, depth?.OfferQuantity, price.Volume, price.OpenInterest, null))
            { Source = "GrowwFeedSample", DepthSourceAtUtc = depth?.SourceAtUtc });
            state.LastRecorded = received;
        }
    }

    private sealed class State(HistoricalFeedInstrument instrument)
    {
        public HistoricalFeedInstrument Instrument { get; } = instrument;
        public GrowwFeedPoint? Price { get; set; }
        public GrowwFeedPoint? Depth { get; set; }
        public DateTimeOffset PriceReceived { get; set; }
        public DateTimeOffset DepthReceived { get; set; }
        public DateTimeOffset LastRecorded { get; set; } = DateTimeOffset.MinValue;
    }
}
