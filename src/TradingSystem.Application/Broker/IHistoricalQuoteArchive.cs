using TradingSystem.Application.Execution;

namespace TradingSystem.Application.Broker;

public sealed record HistoricalQuoteObservation(Guid Id, DateTimeOffset ReceivedAtUtc,
    GrowwQuoteRequest Request, GrowwQuote Quote)
{
    public const string GrowwQuoteReceipt = "GrowwQuoteReceipt";
    public const string GrowwRestFallback = "GrowwRestFallback";
    public const string UpstoxSnapshot = "UpstoxSnapshot";
    public string Source { get; init; } = GrowwQuoteReceipt;
    public DateTimeOffset? DepthSourceAtUtc { get; init; }
    public DateTimeOffset? BrokerObservedAtUtc { get; init; }
    public DateTimeOffset? BackupLiquidityAtUtc { get; init; }
}

public sealed record HistoricalBackupMarketCoverage(int Planned, int Mapped, IReadOnlyList<string> MissingContracts);

public sealed record HistoricalQuoteArchiveDay(IReadOnlyList<HistoricalQuoteObservation> Observations,
    bool HasCaptureGaps);

public sealed record HistoricalReplayQuoteArchiveDay(IReadOnlyList<HistoricalReplayQuote> Quotes, bool HasCaptureGaps)
{
    public IReadOnlyList<HistoricalReplayGap> UnreadableIntervals { get; init; } = [];
    public int BackupSamples { get; init; }
}
public sealed record HistoricalQuoteArchiveHealth(long Buffered, bool GapObserved,
    DateTimeOffset? LastPersistedAtUtc, string? Failure)
{
    public long ArchiveBytes { get; init; }
    public long ArchiveLimitBytes { get; init; }
    public long? DiskFreeBytes { get; init; }
    public IReadOnlyDictionary<string, DateTimeOffset> LastOptionWrittenAtByMarket { get; init; } = new Dictionary<string, DateTimeOffset>();
    public IReadOnlyDictionary<string, DateTimeOffset> LastOptionSourceAtByMarket { get; init; } = new Dictionary<string, DateTimeOffset>();
    public IReadOnlyDictionary<string, DateTimeOffset> LastUpstoxOptionWrittenAtByMarket { get; init; } = new Dictionary<string, DateTimeOffset>();
    public IReadOnlyDictionary<string, HistoricalBackupMarketCoverage> UpstoxCoverage { get; init; } = new Dictionary<string, HistoricalBackupMarketCoverage>();
    public IReadOnlyDictionary<string, string> UpstoxFailures { get; init; } = new Dictionary<string, string>();
}

public static class HistoricalQuoteProjection
{
    public static bool IsValidBackup(HistoricalQuoteObservation value) => value.Source == HistoricalQuoteObservation.UpstoxSnapshot &&
        value.DepthSourceAtUtc is null && value.BrokerObservedAtUtc is { } brokerAt && brokerAt <= value.ReceivedAtUtc &&
        value.ReceivedAtUtc - brokerAt <= TimeSpan.FromSeconds(30) &&
        (value.BackupLiquidityAtUtc is null || value.BackupLiquidityAtUtc <= brokerAt) &&
        GrowwQuoteTimestamp.ToUtc(value.Quote.LastTradeTimeEpochMilliseconds) is { } lastTrade && lastTrade <= brokerAt &&
        value.ReceivedAtUtc - lastTrade <= TimeSpan.FromSeconds(30) && value.Quote.LastPrice > 0m &&
        value.Quote.BidPrice > 0m && value.Quote.OfferPrice >= value.Quote.BidPrice &&
        value.Quote.BidQuantity > 0 && value.Quote.OfferQuantity > 0;

    public static HistoricalReplayQuote ToReplay(HistoricalQuoteObservation value, string symbol) => new(symbol,
        value.ReceivedAtUtc, value.Quote.LastPrice)
    {
        Source = value.Source, BidPrice = value.Quote.BidPrice, OfferPrice = value.Quote.OfferPrice,
        Volume = value.Quote.Volume is >= 0m and <= long.MaxValue ? (long)value.Quote.Volume.Value : null,
        OpenInterest = value.Quote.OpenInterest,
        SourceAtUtc = GrowwQuoteTimestamp.ToUtc(value.Quote.LastTradeTimeEpochMilliseconds),
        BidQuantity = value.Quote.BidQuantity, OfferQuantity = value.Quote.OfferQuantity,
        DepthSourceAtUtc = value.DepthSourceAtUtc, BackupLiquidityAtUtc = value.BackupLiquidityAtUtc
    };
}

public interface IHistoricalQuoteArchive
{
    // Nonblocking observation of a response already obtained by the trading feed.
    // This must not request quotes or throw into the live quote path.
    void TryRecord(HistoricalQuoteObservation observation);
    bool TryRecordBackup(HistoricalQuoteObservation observation) => false;
    void SetBackupCoverage(DateOnly tradingDate, IReadOnlyDictionary<string, HistoricalBackupMarketCoverage> coverage) { }
    void SetBackupFailure(DateOnly tradingDate, string market, string? failure) { }
    void MarkCaptureGap(DateOnly tradingDate);
    HistoricalQuoteArchiveHealth? ReadHealth(DateOnly tradingDate) => null;
    Task<HistoricalQuoteArchiveDay> ReadAsync(DateOnly tradingDate, CancellationToken cancellationToken);
    async Task<HistoricalQuoteArchiveDay> ReadMarketAsync(DateOnly tradingDate, string market, CancellationToken cancellationToken)
    {
        var day = await ReadAsync(tradingDate, cancellationToken);
        return new(day.Observations.Where(value => value.Request.TradingSymbol.StartsWith(market, StringComparison.Ordinal)).ToArray(),
            day.HasCaptureGaps);
    }
    async Task<HistoricalReplayQuoteArchiveDay> ReadReplayAsync(DateOnly tradingDate, string market, string exchange,
        IReadOnlyDictionary<string, string> symbols, DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken token,
        bool streamingOnly = false, bool tolerateUnreadableShards = false, bool includeBackup = false)
    {
        var day = await ReadMarketAsync(tradingDate, market, token);
        var quotes = day.Observations.Where(value => (value.Source == HistoricalQuoteObservation.UpstoxSnapshot ? includeBackup && HistoricalQuoteProjection.IsValidBackup(value) :
            !streamingOnly || value.Source == "GrowwFeedSample") && value.Request.Exchange == exchange &&
            value.ReceivedAtUtc >= startUtc && value.ReceivedAtUtc <= endUtc && symbols.ContainsKey(value.Request.TradingSymbol))
            .Select(value => HistoricalQuoteProjection.ToReplay(value, symbols[value.Request.TradingSymbol])).ToArray();
        return new(quotes, day.HasCaptureGaps) { BackupSamples = quotes.Count(value => value.Source == HistoricalQuoteObservation.UpstoxSnapshot) };
    }
}
