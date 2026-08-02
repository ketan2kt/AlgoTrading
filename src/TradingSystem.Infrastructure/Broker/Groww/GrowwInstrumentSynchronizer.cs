using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TradingSystem.Application.Broker;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.Infrastructure.Broker.Groww;

internal sealed class GrowwInstrumentSynchronizer(
    IGrowwReadOnlyGateway gateway,
    TradingDbContext dbContext,
    TimeProvider timeProvider) : IGrowwInstrumentSynchronizer
{
    public async Task<GrowwInstrumentSyncResult> SynchronizeAsync(
        CancellationToken cancellationToken)
    {
        var downloaded = await gateway.GetInstrumentMasterAsync(cancellationToken);
        var supported = downloaded
            .Select(record => (Record: record, Mapping: TryMap(record)))
            .Where(value => value.Mapping is not null)
            .ToArray();
        var exchanges = supported.Select(value => value.Record.Exchange).Distinct().ToArray();
        var existing = await dbContext.Instruments
            .Where(instrument => exchanges.Contains(instrument.Exchange))
            .ToListAsync(cancellationToken);
        var lookup = existing.ToDictionary(
            instrument => (instrument.Exchange, instrument.TradingSymbol, instrument.Type,
                instrument.ExpiryDate, instrument.StrikePrice));
        var inserted = 0;
        var updated = 0;

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        foreach (var item in supported)
        {
            var mapping = item.Mapping!.Value;
            var strikePrice = item.Record.StrikePrice is > 0 ? item.Record.StrikePrice : null;
            var key = (item.Record.Exchange, item.Record.TradingSymbol, mapping.Type,
                mapping.ExpiryDate, strikePrice);
            if (!lookup.TryGetValue(key, out var instrument))
            {
                instrument = new Instrument(
                    Guid.NewGuid(),
                    item.Record.Exchange,
                    item.Record.TradingSymbol,
                    mapping.Segment,
                    mapping.Type,
                    timeProvider.GetUtcNow());
                dbContext.Instruments.Add(instrument);
                lookup.Add(key, instrument);
                inserted++;
            }
            else
            {
                updated++;
            }

            instrument.UpdateBrokerMetadata(
                item.Record.ExchangeToken,
                mapping.ExpiryDate,
                strikePrice,
                item.Record.LotSize,
                item.Record.TickSize);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new GrowwInstrumentSyncResult(
            downloaded.Count,
            inserted,
            updated,
            downloaded.Count - supported.Length,
            timeProvider.GetUtcNow());
    }

    private static (InstrumentSegment Segment, InstrumentType Type, DateOnly? ExpiryDate)? TryMap(
        GrowwInstrumentRecord record)
    {
        var segment = record.Segment.ToUpperInvariant() switch
        {
            "CASH" => InstrumentSegment.Cash,
            "FNO" => InstrumentSegment.FuturesAndOptions,
            _ => (InstrumentSegment?)null
        };
        var type = record.InstrumentType.ToUpperInvariant() switch
        {
            "EQ" => InstrumentType.Equity,
            "IDX" or "INDEX" => InstrumentType.Index,
            "FUT" => InstrumentType.Future,
            "CE" => InstrumentType.CallOption,
            "PE" => InstrumentType.PutOption,
            _ => (InstrumentType?)null
        };
        if (segment is null || type is null)
        {
            return null;
        }

        DateOnly? expiry = null;
        if (!string.IsNullOrWhiteSpace(record.ExpiryDate))
        {
            if (!DateOnly.TryParseExact(
                    record.ExpiryDate,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var parsedExpiry))
            {
                return null;
            }

            expiry = parsedExpiry;
        }

        return (segment.Value, type.Value, expiry);
    }
}
