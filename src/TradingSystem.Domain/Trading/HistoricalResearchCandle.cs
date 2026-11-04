using TradingSystem.Domain.Common;

namespace TradingSystem.Domain.Trading;

// Research-only historical option path keyed by the provider's stable symbol.
// No live instrument or order relationship is inferred for expired contracts.
public sealed class HistoricalResearchCandle : Entity, IAppendOnlyEntity
{
    public HistoricalResearchCandle(Guid id, string exchange, string underlying,
        string growwSymbol, DateOnly expiryDate, DateTimeOffset openTimeUtc,
        int intervalSeconds, decimal open, decimal high, decimal low, decimal close,
        long volume, decimal? openInterest, string source) : base(id)
    {
        if (string.IsNullOrWhiteSpace(exchange) || string.IsNullOrWhiteSpace(underlying) ||
            string.IsNullOrWhiteSpace(growwSymbol) || string.IsNullOrWhiteSpace(source) ||
            intervalSeconds <= 0 || open <= 0m || close <= 0m || low <= 0m ||
            high < open || high < close || low > open || low > close ||
            volume < 0 || openInterest is < 0m)
            throw new ArgumentException("Historical research candle is invalid.");
        Exchange = exchange;
        Underlying = underlying;
        GrowwSymbol = growwSymbol;
        ExpiryDate = expiryDate;
        OpenTimeUtc = openTimeUtc.ToUniversalTime();
        IntervalSeconds = intervalSeconds;
        Open = open;
        High = high;
        Low = low;
        Close = close;
        Volume = volume;
        OpenInterest = openInterest;
        Source = source;
    }

    public string Exchange { get; private init; }
    public string Underlying { get; private init; }
    public string GrowwSymbol { get; private init; }
    public DateOnly ExpiryDate { get; private init; }
    public DateTimeOffset OpenTimeUtc { get; private init; }
    public int IntervalSeconds { get; private init; }
    public decimal Open { get; private init; }
    public decimal High { get; private init; }
    public decimal Low { get; private init; }
    public decimal Close { get; private init; }
    public long Volume { get; private init; }
    public decimal? OpenInterest { get; private init; }
    public string Source { get; private init; }
}
