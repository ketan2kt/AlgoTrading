using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TradingSystem.Application.Broker;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.Infrastructure.MarketData;

public sealed class GrowwHistoricalCandleImporter(TradingDbContext dbContext)
{
    private static readonly TimeZoneInfo IndiaTimeZone = FindIndiaTimeZone();

    public async Task<int> ImportAsync(Instrument instrument, GrowwHistoricalCandles history,
        DateTimeOffset nowUtc, CancellationToken cancellationToken,
        DateTimeOffset? notBeforeUtc = null, DateTimeOffset? beforeUtc = null,
        bool validateExisting = false)
        => await ImportCoreAsync(instrument, history, nowUtc, notBeforeUtc,
            beforeUtc, validateExisting, retryConcurrentInsert: true, cancellationToken);

    private async Task<int> ImportCoreAsync(Instrument instrument, GrowwHistoricalCandles history,
        DateTimeOffset nowUtc, DateTimeOffset? notBeforeUtc, DateTimeOffset? beforeUtc,
        bool validateExisting, bool retryConcurrentInsert, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        ArgumentNullException.ThrowIfNull(history);
        if (notBeforeUtc.HasValue != beforeUtc.HasValue ||
            (notBeforeUtc.HasValue && notBeforeUtc >= beforeUtc))
            throw new ArgumentException("Historical import range must have ordered endpoints.");
        var intervalSeconds = checked(history.IntervalInMinutes * 60);
        var parsed = history.Candles.Select(value => new
        {
            Source = value,
            OpenTimeUtc = ParseIndiaTimestamp(value.SourceTimestamp)
        }).Where(value => value.OpenTimeUtc.AddSeconds(intervalSeconds) <= nowUtc &&
            (!notBeforeUtc.HasValue || (value.OpenTimeUtc >= notBeforeUtc.Value &&
                value.OpenTimeUtc < beforeUtc!.Value))).ToArray();
        if (parsed.Length == 0) return 0;
        if (parsed.GroupBy(value => value.OpenTimeUtc)
            .Any(group => group.Select(value => value.Source).Distinct().Count() > 1))
            throw new GrowwApiException(
                "Groww historical candles contain conflicting rows for the same minute.",
                "MALFORMED_RESPONSE");
        parsed = parsed.GroupBy(value => value.OpenTimeUtc)
            .Select(group => group.First()).ToArray();

        var first = parsed.Min(value => value.OpenTimeUtc);
        var last = parsed.Max(value => value.OpenTimeUtc);
        var existingQuery = dbContext.Candles.AsNoTracking().Where(value =>
                value.InstrumentId == instrument.Id && value.Source == history.Source &&
                value.IntervalSeconds == intervalSeconds && value.OpenTimeUtc >= first &&
                value.OpenTimeUtc <= last);
        if (validateExisting)
        {
            var stored = await existingQuery.ToDictionaryAsync(value =>
                value.OpenTimeUtc, cancellationToken);
            foreach (var value in parsed)
            {
                if (!stored.TryGetValue(value.OpenTimeUtc, out var prior)) continue;
                if (prior.Open != value.Source.Open || prior.High != value.Source.High ||
                    prior.Low != value.Source.Low || prior.Close != value.Source.Close ||
                    prior.Volume != value.Source.Volume ||
                    prior.OpenInterest != value.Source.OpenInterest)
                    throw new GrowwApiException(
                        "Historical provider revised an archived candle; review before replacing data.",
                        "HISTORICAL_REVISION");
            }
        }
        var existing = await existingQuery.Select(value => value.OpenTimeUtc)
            .ToHashSetAsync(cancellationToken);
        var added = new List<Candle>();
        foreach (var value in parsed.Where(value => !existing.Contains(value.OpenTimeUtc)))
        {
            var candle = new Candle(Guid.NewGuid(), instrument.Id, value.OpenTimeUtc,
                intervalSeconds, value.Source.Open, value.Source.High, value.Source.Low,
                value.Source.Close, value.Source.Volume, history.Source, value.Source.OpenInterest);
            dbContext.Candles.Add(candle);
            added.Add(candle);
        }
        try
        {
            if (added.Count > 0) await dbContext.SaveChangesAsync(cancellationToken);
            return added.Count;
        }
        catch (DbUpdateException error) when (retryConcurrentInsert && error.InnerException is PostgresException
               { SqlState: PostgresErrorCodes.UniqueViolation,
                 ConstraintName: "IX_candles_InstrumentId_OpenTimeUtc_IntervalSeconds_Source" })
        {
            // Native-history warming and ingestion can finish the same minute together.
            // Re-read once, retaining both providers and never overwriting existing OHLC.
            foreach (var candle in added) dbContext.Entry(candle).State = EntityState.Detached;
            return await ImportCoreAsync(instrument, history, nowUtc,
                notBeforeUtc, beforeUtc, validateExisting, retryConcurrentInsert: false, cancellationToken);
        }
    }

    internal static DateTimeOffset ParseIndiaTimestamp(string value)
    {
        if (!DateTime.TryParseExact(value, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var local))
            throw new GrowwApiException("Groww historical candle has an invalid timestamp.",
                "MALFORMED_RESPONSE");
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        return new DateTimeOffset(unspecified, IndiaTimeZone.GetUtcOffset(unspecified)).ToUniversalTime();
    }

    private static TimeZoneInfo FindIndiaTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"); }
        catch (TimeZoneNotFoundException)
        { return TimeZoneInfo.FindSystemTimeZoneById("India Standard Time"); }
    }
}
