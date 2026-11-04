using Microsoft.EntityFrameworkCore;
using TradingSystem.Application.Broker;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Persistence;
using System.Security.Cryptography;
using System.Text.Json;

namespace TradingSystem.Infrastructure.MarketData;

public sealed class GrowwHistoricalResearchImporter(TradingDbContext db)
{
    private const string Source = "GrowwHistoricalApiV1";
    internal const string IndexSource = "GrowwHistoricalIndexApiV1";

    public Task<int> ImportAsync(string exchange, string underlying,
        string growwSymbol, DateOnly expiryDate, GrowwHistoricalCandles history,
        DateTimeOffset startUtc, DateTimeOffset endUtc, DateTimeOffset nowUtc,
        CancellationToken cancellationToken) => ImportCoreAsync(exchange, underlying, growwSymbol,
            expiryDate, history, startUtc, endUtc, nowUtc, Source, cancellationToken);

    public Task<int> ImportMappedAsync(Instrument instrument, string underlying, DateOnly sessionDate,
        GrowwHistoricalCandles history, DateTimeOffset startUtc, DateTimeOffset endUtc, DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(instrument.GrowwSymbol) || !instrument.TradingSymbol.StartsWith(underlying, StringComparison.Ordinal) ||
            instrument.Type is not (InstrumentType.Index or InstrumentType.Future or InstrumentType.CallOption or InstrumentType.PutOption))
            throw new ArgumentException("Verified replay instrument metadata is required.", nameof(instrument));
        // For IndexSource the required legacy date column holds the session
        // date. It is not represented as a derivative expiry in replay.
        return ImportCoreAsync(instrument.Exchange, underlying, instrument.GrowwSymbol,
            instrument.ExpiryDate ?? sessionDate, history, startUtc, endUtc, nowUtc,
            instrument.Type == InstrumentType.Index ? IndexSource : Source, cancellationToken);
    }

    private async Task<int> ImportCoreAsync(string exchange, string underlying,
        string growwSymbol, DateOnly expiryDate, GrowwHistoricalCandles history,
        DateTimeOffset startUtc, DateTimeOffset endUtc, DateTimeOffset nowUtc, string source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (string.IsNullOrWhiteSpace(exchange) ||
            string.IsNullOrWhiteSpace(underlying) ||
            (source != IndexSource && !growwSymbol.StartsWith(exchange + "-" + underlying + "-",
                StringComparison.Ordinal)) ||
            history.IntervalInMinutes != 1 || startUtc >= endUtc)
            throw new ArgumentException("Verified one-minute contract metadata and range are required.");
        var parsed = history.Candles.Select(value => new
        {
            Candle = value,
            OpenTimeUtc = GrowwHistoricalCandleImporter.ParseIndiaTimestamp(
                value.SourceTimestamp)
        }).Where(value => value.OpenTimeUtc >= startUtc &&
            value.OpenTimeUtc < endUtc &&
            value.OpenTimeUtc.AddMinutes(1) <= nowUtc)
            .ToArray();
        if (parsed.GroupBy(value => value.OpenTimeUtc).Any(group =>
                group.Select(value => value.Candle).Distinct().Count() > 1))
            throw new GrowwApiException("Historical contract has conflicting minute rows.",
                "MALFORMED_RESPONSE");
        var unique = parsed.GroupBy(value => value.OpenTimeUtc)
            .Select(group => group.First()).ToArray();
        if (unique.Length == 0) return 0;
        var first = unique.Min(value => value.OpenTimeUtc);
        var last = unique.Max(value => value.OpenTimeUtc);
        var existing = await db.HistoricalResearchCandles.AsNoTracking()
            .Where(value => value.GrowwSymbol == growwSymbol &&
                value.Source == source && value.IntervalSeconds == 60 &&
                value.OpenTimeUtc >= first && value.OpenTimeUtc <= last)
            .ToDictionaryAsync(value => value.OpenTimeUtc, cancellationToken);
        var added = 0;
        foreach (var value in unique)
        {
            if (existing.TryGetValue(value.OpenTimeUtc, out var stored))
            {
                if (stored.Open != value.Candle.Open ||
                    stored.High != value.Candle.High ||
                    stored.Low != value.Candle.Low ||
                    stored.Close != value.Candle.Close ||
                    stored.Volume != value.Candle.Volume ||
                    stored.OpenInterest != value.Candle.OpenInterest ||
                    stored.Exchange != exchange || stored.Underlying != underlying ||
                    stored.ExpiryDate != expiryDate)
                    throw new GrowwApiException(
                        "Historical provider revised an archived candle; review before replacing data.",
                        "HISTORICAL_REVISION");
                continue;
            }
            db.HistoricalResearchCandles.Add(new HistoricalResearchCandle(Guid.NewGuid(),
                exchange, underlying, growwSymbol, expiryDate, value.OpenTimeUtc,
                60, value.Candle.Open, value.Candle.High, value.Candle.Low,
                value.Candle.Close, value.Candle.Volume,
                value.Candle.OpenInterest, source));
            added++;
        }
        db.SystemEvents.Add(new SystemEvent(Guid.NewGuid(), "Information", "HistoricalReplayDataImported",
            "Official historical candles stored in the research-only archive.", nowUtc,
            JsonSerializer.Serialize(new { exchange, underlying, growwSymbol, source, startUtc, endUtc, added,
                fetchedAtUtc = nowUtc, responseFingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(history))) })));
        await db.SaveChangesAsync(cancellationToken);
        return added;
    }
}
