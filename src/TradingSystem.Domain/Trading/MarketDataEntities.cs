using TradingSystem.Domain.Common;

namespace TradingSystem.Domain.Trading;

public sealed class Instrument : MutableEntity
{
    public Instrument(
        Guid id,
        string exchange,
        string tradingSymbol,
        InstrumentSegment segment,
        InstrumentType type,
        DateTimeOffset createdAtUtc) : base(id, createdAtUtc)
    {
        Exchange = exchange;
        TradingSymbol = tradingSymbol;
        Segment = segment;
        Type = type;
    }

    public string Exchange { get; private init; }
    public string TradingSymbol { get; private init; }
    public string? GrowwSymbol { get; private set; }
    public InstrumentSegment Segment { get; private init; }
    public InstrumentType Type { get; private init; }
    public string? ExchangeToken { get; private set; }
    public DateOnly? ExpiryDate { get; private set; }
    public decimal? StrikePrice { get; private set; }
    public int LotSize { get; private set; } = 1;
    public decimal TickSize { get; private set; }
    public bool IsActive { get; private set; } = true;

    public void UpdateBrokerMetadata(
        string exchangeToken,
        string growwSymbol,
        DateOnly? expiryDate,
        decimal? strikePrice,
        int lotSize,
        decimal tickSize)
    {
        if (string.IsNullOrWhiteSpace(exchangeToken))
        {
            throw new ArgumentException("Exchange token is required.", nameof(exchangeToken));
        }
        if (string.IsNullOrWhiteSpace(growwSymbol))
            throw new ArgumentException("Groww symbol is required.", nameof(growwSymbol));

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lotSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tickSize);
        ExchangeToken = exchangeToken;
        GrowwSymbol = growwSymbol;
        ExpiryDate = expiryDate;
        StrikePrice = strikePrice;
        LotSize = lotSize;
        TickSize = tickSize;
        IsActive = true;
    }

    public void UpdateIndexBrokerMetadata(string exchangeToken, string growwSymbol)
    {
        if (Type != InstrumentType.Index)
        {
            throw new InvalidOperationException("Index metadata can only be applied to an index instrument.");
        }

        if (string.IsNullOrWhiteSpace(exchangeToken))
        {
            throw new ArgumentException("Exchange token is required.", nameof(exchangeToken));
        }
        if (string.IsNullOrWhiteSpace(growwSymbol))
            throw new ArgumentException("Groww symbol is required.", nameof(growwSymbol));

        ExchangeToken = exchangeToken;
        GrowwSymbol = growwSymbol;
        IsActive = true;
    }
}

public sealed class Candle : Entity, IAppendOnlyEntity
{
    public Candle(
        Guid id,
        Guid instrumentId,
        DateTimeOffset openTimeUtc,
        int intervalSeconds,
        decimal open,
        decimal high,
        decimal low,
        decimal close,
        long volume,
        string source,
        decimal? openInterest = null) : base(id)
    {
        InstrumentId = instrumentId;
        OpenTimeUtc = openTimeUtc.ToUniversalTime();
        IntervalSeconds = intervalSeconds;
        Open = open;
        High = high;
        Low = low;
        Close = close;
        Volume = volume;
        Source = source;
        OpenInterest = openInterest;
        if (intervalSeconds <= 0 || open <= 0 || high < open || high < close ||
            low > open || low > close || low <= 0 || volume < 0)
        {
            throw new ArgumentException("Candle values are invalid.");
        }
    }

    public Guid InstrumentId { get; private init; }
    public DateTimeOffset OpenTimeUtc { get; private init; }
    public int IntervalSeconds { get; private init; }
    public decimal Open { get; private init; }
    public decimal High { get; private init; }
    public decimal Low { get; private init; }
    public decimal Close { get; private init; }
    public long Volume { get; private init; }
    public decimal? OpenInterest { get; private init; }
    public string Source { get; private init; }
    public bool IsValid { get; private init; } = true;
}

public abstract class DataSnapshot : Entity, IAppendOnlyEntity
{
    protected DataSnapshot(
        Guid id,
        string source,
        DateTimeOffset sourceTimestampUtc,
        DateTimeOffset receivedAtUtc,
        string payloadJson) : base(id)
    {
        Source = source;
        SourceTimestampUtc = sourceTimestampUtc.ToUniversalTime();
        ReceivedAtUtc = receivedAtUtc.ToUniversalTime();
        PayloadJson = payloadJson;
    }

    public string Source { get; private init; }
    public DateTimeOffset SourceTimestampUtc { get; private init; }
    public DateTimeOffset ReceivedAtUtc { get; private init; }
    public string PayloadJson { get; private init; }
    public bool IsFresh { get; private init; }
    public bool IsValid { get; private init; }
    public decimal Confidence { get; private init; }
    public string? ErrorCode { get; private init; }
}

public sealed class OptionChainSnapshot : DataSnapshot
{
    public OptionChainSnapshot(
        Guid id,
        Guid underlyingInstrumentId,
        DateOnly expiryDate,
        string source,
        DateTimeOffset sourceTimestampUtc,
        DateTimeOffset receivedAtUtc,
        string payloadJson)
        : base(id, source, sourceTimestampUtc, receivedAtUtc, payloadJson)
    {
        UnderlyingInstrumentId = underlyingInstrumentId;
        ExpiryDate = expiryDate;
    }

    public Guid UnderlyingInstrumentId { get; private init; }
    public DateOnly ExpiryDate { get; private init; }
}

public sealed class ExternalMarketSnapshot : DataSnapshot
{
    public ExternalMarketSnapshot(
        Guid id,
        string providerType,
        string source,
        DateTimeOffset sourceTimestampUtc,
        DateTimeOffset receivedAtUtc,
        string payloadJson)
        : base(id, source, sourceTimestampUtc, receivedAtUtc, payloadJson)
    {
        ProviderType = providerType;
    }

    public string ProviderType { get; private init; }
}

public sealed class MarketRegimeSnapshot : Entity, IAppendOnlyEntity
{
    public MarketRegimeSnapshot(
        Guid id,
        Guid tradingSessionId,
        MarketRegime regime,
        Direction? bias,
        decimal confidence,
        decimal dataQuality,
        bool tradingPermitted,
        string explanationJson,
        DateTimeOffset observedAtUtc) : base(id)
    {
        TradingSessionId = tradingSessionId;
        Regime = regime;
        Bias = bias;
        Confidence = confidence;
        DataQuality = dataQuality;
        TradingPermitted = tradingPermitted;
        ExplanationJson = explanationJson;
        ObservedAtUtc = observedAtUtc.ToUniversalTime();
    }

    public Guid TradingSessionId { get; private init; }
    public MarketRegime Regime { get; private init; }
    public Direction? Bias { get; private init; }
    public decimal Confidence { get; private init; }
    public decimal DataQuality { get; private init; }
    public bool TradingPermitted { get; private init; }
    public string ExplanationJson { get; private init; }
    public DateTimeOffset ObservedAtUtc { get; private init; }
}
