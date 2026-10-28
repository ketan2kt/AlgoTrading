using System.Globalization;

namespace TradingSystem.Application.MarketData;

public sealed record TreasuryTenYearYield(DateOnly Date, decimal Percent,
    decimal? PreviousPercent);

public static class TreasuryYieldParser
{
    public static TreasuryTenYearYield? ParseLatestTenYear(string csv, DateOnly asOf)
    {
        var lines = csv.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2) return null;
        var headers = lines[0].Split(',').Select(value =>
            value.Trim().Trim('"').TrimStart('\uFEFF')).ToArray();
        var dateIndex = Array.FindIndex(headers, value => value.Equals("Date",
            StringComparison.OrdinalIgnoreCase));
        var yieldIndex = Array.FindIndex(headers, value => value.Equals("10 Yr",
            StringComparison.OrdinalIgnoreCase));
        if (dateIndex < 0 || yieldIndex < 0) return null;

        var observations = new List<(DateOnly Date, decimal Yield)>();
        foreach (var line in lines.Skip(1))
        {
            var fields = line.Split(',');
            if (fields.Length <= Math.Max(dateIndex, yieldIndex) ||
                !DateOnly.TryParseExact(fields[dateIndex].Trim().Trim('"'), "MM/dd/yyyy",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ||
                date > asOf ||
                !decimal.TryParse(fields[yieldIndex].Trim().Trim('"'), NumberStyles.Number,
                    CultureInfo.InvariantCulture, out var percent) || percent <= 0) continue;
            observations.Add((date, percent));
        }
        var recent = observations.OrderByDescending(value => value.Date).Take(2).ToArray();
        return recent.Length == 0 ? null : new(recent[0].Date, recent[0].Yield,
            recent.Length > 1 ? recent[1].Yield : null);
    }
}
