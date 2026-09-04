using System.Text.Json;

namespace TradingSystem.Application.Execution;

public static class SensexResearchAuditParser
{
    public static (Guid PositionId, decimal Value)? Parse(string json, string field)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty("positionId", out var id) && id.ValueKind == JsonValueKind.String &&
                id.TryGetGuid(out var guid) && root.TryGetProperty(field, out var value) &&
                value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number))
                return (guid, number);
        }
        catch (JsonException) { }
        return null;
    }
}
