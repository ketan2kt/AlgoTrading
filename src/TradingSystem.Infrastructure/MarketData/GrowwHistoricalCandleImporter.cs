using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TradingSystem.Application.Broker;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.Infrastructure.MarketData;

internal sealed class GrowwHistoricalCandleImporter(TradingDbContext dbContext)
{
    private static readonly TimeZoneInfo IndiaTimeZone = FindIndiaTimeZone();

    public async Task<int> ImportAsync(Instrument instrument, GrowwHistoricalCandles history,
        DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        ArgumentNullException.ThrowIfNull(history);
        var intervalSeconds = checked(history.IntervalInMinutes * 60);
        var parsed = history.Candles.Select(value => new
        {
            Source = value,
            OpenTimeUtc = ParseIndiaTimestamp(value.SourceTimestamp)
        }).Where(value => value.OpenTimeUtc.AddSeconds(intervalSeconds) <= nowUtc).ToArray();
        if (parsed.Length == 0) return 0;

        var first = parsed.Min(value => value.OpenTimeUtc);
        var last = parsed.Max(value => value.OpenTimeUtc);
        var existing = await dbContext.Candles.AsNoTracking().Where(value =>
                value.InstrumentId == instrument.Id && value.Source == "Groww" &&
                value.IntervalSeconds == intervalSeconds && value.OpenTimeUtc >= first &&
                value.OpenTimeUtc <= last)
            .Select(value => value.OpenTimeUtc).ToHashSetAsync(cancellationToken);
        foreach (var value in parsed.Where(value => !existing.Contains(value.OpenTimeUtc)))
        {
            dbContext.Candles.Add(new Candle(Guid.NewGuid(), instrument.Id, value.OpenTimeUtc,
                intervalSeconds, value.Source.Open, value.Source.High, value.Source.Low,
                value.Source.Close, value.Source.Volume, "Groww", value.Source.OpenInterest));
        }

        var inserted = dbContext.ChangeTracker.Entries<Candle>()
            .Count(value => value.State == EntityState.Added);
        if (inserted > 0) await dbContext.SaveChangesAsync(cancellationToken);
        return inserted;
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
