using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;
using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Execution;

public sealed record HistoricalInstrumentSnapshotItem(
    string Exchange, string TradingSymbol, string GrowwSymbol,
    InstrumentSegment Segment, InstrumentType Type, DateOnly? ExpiryDate,
    decimal? StrikePrice, int LotSize, decimal TickSize);

public sealed record HistoricalInstrumentMasterManifest(string PayloadJson,
    string Fingerprint, int ContractCount);

public static class HistoricalInstrumentMasterFingerprint
{
    public static HistoricalInstrumentMasterManifest Create(
        IReadOnlyList<HistoricalInstrumentSnapshotItem> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Count == 0 || source.Any(item =>
                string.IsNullOrWhiteSpace(item.Exchange) ||
                string.IsNullOrWhiteSpace(item.TradingSymbol) ||
                string.IsNullOrWhiteSpace(item.GrowwSymbol) ||
                item.Type != InstrumentType.Index &&
                (item.LotSize <= 0 || item.TickSize <= 0m)) ||
            source.GroupBy(item => (item.Exchange, item.TradingSymbol,
                    item.Type, item.ExpiryDate, item.StrikePrice))
                .Any(group => group.Count() > 1))
            throw new ArgumentException("A unique, validated instrument master is required.",
                nameof(source));
        var ordered = source.OrderBy(item => item.Exchange, StringComparer.Ordinal)
            .ThenBy(item => item.TradingSymbol, StringComparer.Ordinal)
            .ThenBy(item => item.Type)
            .ThenBy(item => item.ExpiryDate)
            .ThenBy(item => item.StrikePrice).ToArray();
        var json = JsonSerializer.Serialize(ordered);
        var canonical = string.Join('\n', ordered.Select(item => string.Join('|',
            item.Exchange, item.TradingSymbol, item.GrowwSymbol,
            item.Segment.ToString(), item.Type.ToString(),
            item.ExpiryDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
            item.StrikePrice?.ToString("G29", CultureInfo.InvariantCulture) ?? "",
            item.LotSize.ToString(CultureInfo.InvariantCulture),
            item.TickSize.ToString("G29", CultureInfo.InvariantCulture)))) + "\n";
        var fingerprint = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(canonical)));
        return new(json, fingerprint, ordered.Length);
    }
}
