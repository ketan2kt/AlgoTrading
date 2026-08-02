namespace TradingSystem.Application.Broker;

public interface IGrowwReadOnlyGateway
{
    Task<GrowwUserProfile> GetUserProfileAsync(CancellationToken cancellationToken);

    Task<GrowwQuote> GetQuoteAsync(
        GrowwQuoteRequest request,
        CancellationToken cancellationToken);

    Task<GrowwHistoricalCandles> GetHistoricalCandlesAsync(
        GrowwHistoricalCandleRequest request,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<GrowwInstrumentRecord>> GetInstrumentMasterAsync(
        CancellationToken cancellationToken);
}

public sealed record GrowwUserProfile(
    string VendorUserId,
    string Ucc,
    bool NseEnabled,
    bool BseEnabled,
    bool DdpiEnabled,
    IReadOnlyList<string> ActiveSegments);

public sealed record GrowwQuoteRequest(string Exchange, string Segment, string TradingSymbol);

public sealed record GrowwQuote(
    decimal LastPrice,
    long LastTradeTimeEpochMilliseconds,
    decimal? BidPrice,
    long? BidQuantity,
    decimal? OfferPrice,
    long? OfferQuantity,
    decimal? Volume,
    decimal? OpenInterest,
    decimal? OpenInterestDayChange);

public sealed record GrowwHistoricalCandleRequest(
    string Exchange,
    string Segment,
    string GrowwSymbol,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string CandleInterval);

public sealed record GrowwHistoricalCandle(
    string SourceTimestamp,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    long Volume,
    decimal? OpenInterest);

public sealed record GrowwHistoricalCandles(
    IReadOnlyList<GrowwHistoricalCandle> Candles,
    decimal ClosingPrice,
    int IntervalInMinutes);

public sealed record GrowwInstrumentRecord(
    string Exchange,
    string ExchangeToken,
    string TradingSymbol,
    string GrowwSymbol,
    string InstrumentType,
    string Segment,
    string? Isin,
    string? UnderlyingSymbol,
    string? ExpiryDate,
    decimal? StrikePrice,
    int LotSize,
    decimal TickSize,
    bool BuyAllowed,
    bool SellAllowed);

public sealed class GrowwApiException(
    string message,
    string? errorCode = null,
    int? statusCode = null,
    Exception? innerException = null) : Exception(message, innerException)
{
    public string? ErrorCode { get; } = errorCode;
    public int? StatusCode { get; } = statusCode;
}
