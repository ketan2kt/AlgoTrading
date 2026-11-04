using TradingSystem.Application.Execution;

namespace TradingSystem.Application.Broker;

public sealed record HistoricalQuoteObservation(Guid Id, DateTimeOffset ReceivedAtUtc,
    GrowwQuoteRequest Request, GrowwQuote Quote)
{
    public string Source { get; init; } = "GrowwQuoteReceipt";
    public DateTimeOffset? DepthSourceAtUtc { get; init; }
}

public sealed record HistoricalQuoteArchiveDay(IReadOnlyList<HistoricalQuoteObservation> Observations,
    bool HasCaptureGaps);

public sealed record HistoricalReplayQuoteArchiveDay(IReadOnlyList<HistoricalReplayQuote> Quotes, bool HasCaptureGaps);

public static class HistoricalQuoteProjection
{
    public static HistoricalReplayQuote ToReplay(HistoricalQuoteObservation value, string symbol) => new(symbol,
        value.ReceivedAtUtc, value.Quote.LastPrice)
    {
        Source = value.Source, BidPrice = value.Quote.BidPrice, OfferPrice = value.Quote.OfferPrice,
        Volume = value.Quote.Volume is >= 0m and <= long.MaxValue ? (long)value.Quote.Volume.Value : null,
        OpenInterest = value.Quote.OpenInterest,
        SourceAtUtc = value.Quote.LastTradeTimeEpochMilliseconds is > 0 and <= 253402300799999
            ? DateTimeOffset.FromUnixTimeMilliseconds(value.Quote.LastTradeTimeEpochMilliseconds.Value) : null,
        DepthSourceAtUtc = value.DepthSourceAtUtc
    };
}

public interface IHistoricalQuoteArchive
{
    // Nonblocking observation of a response already obtained by the trading feed.
    // This must not request quotes or throw into the live quote path.
    void TryRecord(HistoricalQuoteObservation observation);
    void MarkCaptureGap(DateOnly tradingDate);
    Task<HistoricalQuoteArchiveDay> ReadAsync(DateOnly tradingDate, CancellationToken cancellationToken);
    async Task<HistoricalQuoteArchiveDay> ReadMarketAsync(DateOnly tradingDate, string market, CancellationToken cancellationToken)
    {
        var day = await ReadAsync(tradingDate, cancellationToken);
        return new(day.Observations.Where(value => value.Request.TradingSymbol.StartsWith(market, StringComparison.Ordinal)).ToArray(),
            day.HasCaptureGaps);
    }
    async Task<HistoricalReplayQuoteArchiveDay> ReadReplayAsync(DateOnly tradingDate, string market, string exchange,
        IReadOnlyDictionary<string, string> symbols, DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken token)
    {
        var day = await ReadMarketAsync(tradingDate, market, token);
        return new(day.Observations.Where(value => value.Request.Exchange == exchange &&
            value.ReceivedAtUtc >= startUtc && value.ReceivedAtUtc <= endUtc && symbols.ContainsKey(value.Request.TradingSymbol))
            .Select(value => HistoricalQuoteProjection.ToReplay(value, symbols[value.Request.TradingSymbol])).ToArray(), day.HasCaptureGaps);
    }
}
