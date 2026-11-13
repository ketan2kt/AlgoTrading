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
        var ordered = OrderAndValidate(source);
        return new(JsonSerializer.Serialize(ordered), ComputeFingerprint(ordered), ordered.Length);
    }

    public static (string Fingerprint, int ContractCount) Verify(IReadOnlyList<HistoricalInstrumentSnapshotItem> source)
    {
        var ordered = OrderAndValidate(source);
        return (ComputeFingerprint(ordered), ordered.Length);
    }

    private static HistoricalInstrumentSnapshotItem[] OrderAndValidate(IReadOnlyList<HistoricalInstrumentSnapshotItem> source)
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
        return source.OrderBy(item => item.Exchange, StringComparer.Ordinal)
            .ThenBy(item => item.TradingSymbol, StringComparer.Ordinal)
            .ThenBy(item => item.Type)
            .ThenBy(item => item.ExpiryDate)
            .ThenBy(item => item.StrikePrice).ToArray();
    }

    private static string ComputeFingerprint(IReadOnlyList<HistoricalInstrumentSnapshotItem> ordered)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var item in ordered)
            hash.AppendData(Encoding.UTF8.GetBytes(string.Join('|',
            item.Exchange, item.TradingSymbol, item.GrowwSymbol,
            item.Segment.ToString(), item.Type.ToString(),
            item.ExpiryDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
            item.StrikePrice?.ToString("G29", CultureInfo.InvariantCulture) ?? "",
            item.LotSize.ToString(CultureInfo.InvariantCulture),
            item.TickSize.ToString("G29", CultureInfo.InvariantCulture)) + "\n"));
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
