using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TradingSystem.Application.Broker;
using TradingSystem.Application.Execution;
using TradingSystem.Application.MarketData;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.Infrastructure.Broker.Groww;

internal sealed class GrowwInstrumentSynchronizer(
    IGrowwReadOnlyGateway gateway,
    TradingDbContext dbContext,
    TimeProvider timeProvider) : IGrowwInstrumentSynchronizer,
        IHistoricalInstrumentMasterCapturer
{
    public async Task<GrowwInstrumentSyncResult> SynchronizeAsync(
        CancellationToken cancellationToken)
    {
        var downloaded = await gateway.GetInstrumentMasterAsync(cancellationToken);
        var supported = SelectRequiredTradingRecords(downloaded)
            .Select(record => (Record: record, Mapping: TryMap(record)))
            .ToArray();
        if (supported.Count(value => value.Mapping!.Value.Type == InstrumentType.Index &&
                                    value.Record.Exchange == "NSE" &&
                                    value.Record.TradingSymbol == "NIFTY") != 1)
        {
            throw new GrowwApiException(
                "Groww instrument master did not contain exactly one valid NSE NIFTY cash index.",
                "MALFORMED_RESPONSE");
        }

        var exchanges = supported.Select(value => value.Record.Exchange).Distinct().ToArray();
        var existing = await dbContext.Instruments
            .Where(instrument => exchanges.Contains(instrument.Exchange))
            .ToListAsync(cancellationToken);
        var lookup = existing.ToDictionary(
            instrument => (instrument.Exchange, instrument.TradingSymbol, instrument.Type,
                instrument.ExpiryDate, instrument.StrikePrice));
        var inserted = 0;
        var updated = 0;

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

            if (mapping.Type == InstrumentType.Index)
            {
                instrument.UpdateIndexBrokerMetadata(item.Record.ExchangeToken, item.Record.GrowwSymbol);
            }
            else
            {
                instrument.UpdateBrokerMetadata(
                    item.Record.ExchangeToken,
                    item.Record.GrowwSymbol,
                    mapping.ExpiryDate,
                    strikePrice,
                    item.Record.LotSize ?? throw Missing(item.Record, "lot_size"),
                    InstrumentPriceIncrement.FromGrowwInstrumentMaster(
                        item.Record.TickSize ?? throw Missing(item.Record, "tick_size")));
            }
        }

        await AddSnapshotIfNewAsync(supported, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new GrowwInstrumentSyncResult(
            downloaded.Count,
            inserted,
            updated,
            downloaded.Count - supported.Length,
            timeProvider.GetUtcNow());
    }

    public async Task CaptureAsync(CancellationToken cancellationToken)
    {
        var downloaded = await gateway.GetInstrumentMasterAsync(cancellationToken);
        var supported = SelectRequiredTradingRecords(downloaded)
            .Select(record => (Record: record, Mapping: TryMap(record)))
            .ToArray();
        if (supported.Count(value => value.Mapping!.Value.Type == InstrumentType.Index &&
                value.Record.Exchange == "NSE" &&
                value.Record.TradingSymbol == "NIFTY") != 1)
            throw new GrowwApiException(
                "Groww instrument master did not contain exactly one valid NSE NIFTY cash index.",
                "MALFORMED_RESPONSE");
        await AddSnapshotIfNewAsync(supported, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task AddSnapshotIfNewAsync(
        (GrowwInstrumentRecord Record,
            (InstrumentSegment Segment, InstrumentType Type, DateOnly? ExpiryDate)? Mapping)[] supported,
        CancellationToken cancellationToken)
    {
        // Preserve the exact dated metadata observed from this official master.
        // Mutable Instrument rows alone cannot prove a past lot/expiry/tick.
        var snapshotItems = supported.Select(item => new HistoricalInstrumentSnapshotItem(
                item.Record.Exchange, item.Record.TradingSymbol,
                item.Record.GrowwSymbol, item.Mapping!.Value.Segment,
                item.Mapping.Value.Type, item.Mapping.Value.ExpiryDate,
                item.Record.StrikePrice is > 0m ? item.Record.StrikePrice : null,
                item.Mapping.Value.Type == InstrumentType.Index ? 1 :
                    item.Record.LotSize ?? throw Missing(item.Record, "lot_size"),
                item.Mapping.Value.Type == InstrumentType.Index ? 0m :
                    InstrumentPriceIncrement.FromGrowwInstrumentMaster(
                        item.Record.TickSize ?? throw Missing(item.Record, "tick_size"))))
            .ToArray();
        var snapshot = HistoricalInstrumentMasterFingerprint.Create(snapshotItems);
        var observedAt = timeProvider.GetUtcNow();
        var sessionDate = DateOnly.FromDateTime(observedAt
            .ToOffset(TimeSpan.FromHours(5.5)).DateTime);
        const string source = "GrowwInstrumentMasterV1";
        if (!await dbContext.HistoricalInstrumentMasterSnapshots.AsNoTracking()
            .AnyAsync(value => value.SessionDate == sessionDate &&
                value.Source == source && value.Fingerprint == snapshot.Fingerprint,
                cancellationToken))
            dbContext.HistoricalInstrumentMasterSnapshots.Add(new(
                Guid.NewGuid(), sessionDate, observedAt, source,
                snapshot.Fingerprint, snapshot.ContractCount, snapshot.PayloadJson));
    }

    internal static IReadOnlyList<GrowwInstrumentRecord> SelectSupportedUniqueRecords(
        IReadOnlyList<GrowwInstrumentRecord> downloaded)
    {
        var mapped = downloaded
            .Select(record => (Record: record, Mapping: TryMap(record)))
            .Where(value => value.Mapping is not null)
            .ToArray();
        // Groww's master can contain distinct securities which collapse to the same
        // key supported by our current domain model (for example, bonds sharing a
        // trading symbol but having different ISINs). Select one deterministically
        // so an unrelated duplicate cannot block synchronisation of NIFTY.
        return mapped
            .GroupBy(value => (
                value.Record.Exchange,
                value.Record.TradingSymbol,
                value.Mapping!.Value.Type,
                value.Mapping.Value.ExpiryDate,
                StrikePrice: value.Record.StrikePrice is > 0 ? value.Record.StrikePrice : null))
            .Select(group => group
                .OrderBy(value => value.Record.ExchangeToken, StringComparer.Ordinal)
                .First().Record)
            .ToArray();
    }

    internal static IReadOnlyList<GrowwInstrumentRecord> SelectRequiredNiftyRecords(
        IReadOnlyList<GrowwInstrumentRecord> downloaded) =>
        SelectSupportedUniqueRecords(downloaded).Where(IsRequiredNiftyInstrument).ToArray();

    internal static IReadOnlyList<GrowwInstrumentRecord> SelectRequiredTradingRecords(
        IReadOnlyList<GrowwInstrumentRecord> downloaded) =>
        SelectSupportedUniqueRecords(downloaded).Where(record =>
            IsRequiredNiftyInstrument(record) || IsRequiredSensexInstrument(record) ||
            IsRequiredBankIndexInstrument(record, "NSE", "BANKNIFTY") ||
            IsRequiredNaturalGasInstrument(record)).ToArray();

    private static bool IsRequiredBankIndexInstrument(GrowwInstrumentRecord record,
        string exchange, string underlying)
    {
        if (!string.Equals(record.Exchange, exchange, StringComparison.OrdinalIgnoreCase)) return false;
        if (string.Equals(record.Segment, "CASH", StringComparison.OrdinalIgnoreCase))
            return string.Equals(record.InstrumentType, "IDX", StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(record.TradingSymbol, underlying, StringComparison.OrdinalIgnoreCase);
        return string.Equals(record.Segment, "FNO", StringComparison.OrdinalIgnoreCase) &&
               record.InstrumentType is "CE" or "PE" or "FUT" &&
               string.Equals(record.UnderlyingSymbol, underlying, StringComparison.OrdinalIgnoreCase) &&
               !string.IsNullOrWhiteSpace(record.ExpiryDate);
    }

    private static bool IsRequiredNiftyInstrument(GrowwInstrumentRecord record)
    {
        if (!string.Equals(record.Exchange, "NSE", StringComparison.OrdinalIgnoreCase)) return false;
        var isIndex = string.Equals(record.Segment, "CASH", StringComparison.OrdinalIgnoreCase) &&
                      string.Equals(record.InstrumentType, "IDX", StringComparison.OrdinalIgnoreCase) &&
                      string.Equals(record.TradingSymbol, "NIFTY", StringComparison.OrdinalIgnoreCase);
        var isOption = string.Equals(record.Segment, "FNO", StringComparison.OrdinalIgnoreCase) &&
                       (string.Equals(record.InstrumentType, "CE", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(record.InstrumentType, "PE", StringComparison.OrdinalIgnoreCase)) &&
                       string.Equals(record.UnderlyingSymbol, "NIFTY", StringComparison.OrdinalIgnoreCase) &&
                       !string.IsNullOrWhiteSpace(record.ExpiryDate);
        var isFuture = string.Equals(record.Segment, "FNO", StringComparison.OrdinalIgnoreCase) &&
                       string.Equals(record.InstrumentType, "FUT", StringComparison.OrdinalIgnoreCase) &&
                       string.Equals(record.UnderlyingSymbol, "NIFTY", StringComparison.OrdinalIgnoreCase) &&
                       !string.IsNullOrWhiteSpace(record.ExpiryDate);
        return isIndex || isOption || isFuture;
    }

    private static bool IsRequiredSensexInstrument(GrowwInstrumentRecord record)
    {
        if (!string.Equals(record.Exchange, "BSE", StringComparison.OrdinalIgnoreCase)) return false;
        var isIndex = string.Equals(record.Segment, "CASH", StringComparison.OrdinalIgnoreCase) &&
                      string.Equals(record.InstrumentType, "IDX", StringComparison.OrdinalIgnoreCase) &&
                      string.Equals(record.TradingSymbol, "SENSEX", StringComparison.OrdinalIgnoreCase);
        var isDerivative = string.Equals(record.Segment, "FNO", StringComparison.OrdinalIgnoreCase) &&
                           record.InstrumentType is "CE" or "PE" or "FUT" &&
                           string.Equals(record.UnderlyingSymbol, "SENSEX", StringComparison.OrdinalIgnoreCase) &&
                           !string.IsNullOrWhiteSpace(record.ExpiryDate);
        return isIndex || isDerivative;
    }

    private static bool IsRequiredNaturalGasInstrument(GrowwInstrumentRecord record) =>
        string.Equals(record.Exchange, "MCX", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(record.Segment, "COMMODITY", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(record.InstrumentType, "FUT", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(record.UnderlyingSymbol, "NATGASMINI", StringComparison.OrdinalIgnoreCase) &&
        record.TradingSymbol.StartsWith("NATGASMINI", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(record.ExpiryDate);

    private static GrowwApiException Missing(GrowwInstrumentRecord record, string field) =>
        new($"Groww instrument {record.GrowwSymbol} omitted required {field}.", "MALFORMED_RESPONSE");

    private static (InstrumentSegment Segment, InstrumentType Type, DateOnly? ExpiryDate)? TryMap(
        GrowwInstrumentRecord record)
    {
        var segment = record.Segment.ToUpperInvariant() switch
        {
            "CASH" => InstrumentSegment.Cash,
            "FNO" => InstrumentSegment.FuturesAndOptions,
            "COMMODITY" => InstrumentSegment.Commodity,
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
