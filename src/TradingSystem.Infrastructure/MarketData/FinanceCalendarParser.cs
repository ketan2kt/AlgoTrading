using System.Globalization;
using System.Text.Json;
using TradingSystem.Application.MarketData;

namespace TradingSystem.Infrastructure.MarketData;

internal static class FinanceCalendarParser
{
    private static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);

    internal static PreSessionEvidence? Parse(string json, DateTimeOffset capturedAtUtc)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("events", out var events) ||
            events.ValueKind != JsonValueKind.Array) return null;

        var upcoming = new List<(string Name, string Country, DateTimeOffset At)>();
        DateTimeOffset? latestVerification = null;
        var validRows = 0;
        foreach (var item in events.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                (item.TryGetProperty("data_stale", out var stale) && stale.ValueKind == JsonValueKind.True))
                continue;
            var name = ReadString(item, "name");
            var country = ReadString(item, "country");
            var impact = ReadString(item, "impact");
            var status = ReadString(item, "status");
            var time = ReadString(item, "release_at_utc");
            if (name is null || country is null || impact != "high" || status != "confirmed" ||
                time is null || !DateTimeOffset.TryParse(time, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var at)) continue;
            if (ReadString(item, "last_verified_at") is { } verified &&
                DateTimeOffset.TryParse(verified, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var verifiedAt))
            {
                if (verifiedAt > capturedAtUtc.AddMinutes(1) ||
                    verifiedAt < capturedAtUtc.AddDays(-30)) continue;
                if (latestVerification is null || verifiedAt > latestVerification)
                    latestVerification = verifiedAt;
            }
            validRows++;
            if (at < capturedAtUtc || at > capturedAtUtc.AddHours(36)) continue;
            upcoming.Add((name, country, at));
        }
        if (upcoming.Count == 0)
        {
            if (validRows == 0) return null;
            return new("Scheduled events", "Published",
                "No confirmed high-impact release found in the next 36 hours across covered countries.",
                "Finance Calendar", latestVerification,
                "Major global releases only; Indian and minor events may be absent. Source: financecalendar.com.");
        }
        var value = string.Join("; ", upcoming.OrderBy(value => value.At).Take(6)
            .Select(value => $"{value.Country} {value.Name} " +
                $"{value.At.ToOffset(IstOffset):dd MMM HH:mm} IST"));
        return new("Scheduled events", "Published", value, "Finance Calendar",
            latestVerification,
            "Confirmed high-impact global releases in the next 36 hours; not a complete Indian calendar. Source: financecalendar.com.");
    }

    private static string? ReadString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ?
            value.GetString() : null;
}
