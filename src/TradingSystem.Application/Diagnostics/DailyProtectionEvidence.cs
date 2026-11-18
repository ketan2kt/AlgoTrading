using System.Text.Json;

namespace TradingSystem.Application.Diagnostics;

public static class DailyProtectionEvidence
{
    public sealed record Receipt(Guid SourceId, DailyProtectionReceipt Evidence);

    public static Receipt? Parse(DateTimeOffset at, string outcome, string json)
    {
        if (!outcome.StartsWith("BankNiftyConfirmedTrail:", StringComparison.Ordinal) &&
            !outcome.StartsWith("ProfitProtection:", StringComparison.Ordinal) &&
            !outcome.StartsWith("TrailingStop:", StringComparison.Ordinal) &&
            !outcome.StartsWith("PositionSample:", StringComparison.Ordinal) &&
            !outcome.StartsWith("PaperStopSize:", StringComparison.Ordinal)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            JsonElement Value(string name) => r.ValueKind == JsonValueKind.Object
                ? r.EnumerateObject().FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value : default;
            decimal? Number(string name) => Value(name) is var v && v.ValueKind == JsonValueKind.Number &&
                v.TryGetDecimal(out var n) ? n : null;
            string? Text(string name) => Value(name) is var v && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var idText = Text("positionId") ?? Text("signalId") ?? outcome.Split(':').ElementAtOrDefault(1);
            if (!Guid.TryParse(idText, out var id)) return null;
            var bank = outcome.StartsWith("BankNiftyConfirmedTrail:", StringComparison.Ordinal);
            var floor = Number("floorNet");
            var stage = Text("stage") ?? Text("protectionStage") ?? (bank
                ? floor == 0 ? "Conditional break-even" : floor > 0 ? "70% net-profit trail" : "Unarmed"
                : "Stage not recorded");
            // Nifty audit rows are minute-bucketed; use the actual observation
            // receipt so evidence from the entry minute is not filtered out.
            if (Value("observedAtUtc") is var observation && observation.ValueKind == JsonValueKind.String &&
                observation.TryGetDateTimeOffset(out var observedAt)) at = observedAt;
            return new(id, new(at, outcome.Split(':')[0], stage, floor,
                Number("effectiveStop") ?? Number("newStop") ?? Number("stopPrice") ?? Number("stop"), Number("bid"),
                Value("triggered").ValueKind == JsonValueKind.True, Text("policyVersion")));
        }
        catch (JsonException) { return null; }
    }
}
