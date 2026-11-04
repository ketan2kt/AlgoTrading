using System.Globalization;

namespace TradingSystem.Application.Execution;

// Selects a deliberately generous strike corridor around the entire observed
// index session. A listed contract alone is not proof that it traded on the
// session date; replay must still require historical bars at decision time.
public static class HistoricalOptionUniverse
{
    public static IReadOnlyList<string> Select(
        IReadOnlyList<string> growwContracts, string exchange, string underlying,
        DateOnly expiry, decimal sessionLow, decimal sessionHigh, decimal padding)
    {
        ArgumentNullException.ThrowIfNull(growwContracts);
        if (string.IsNullOrWhiteSpace(exchange) || string.IsNullOrWhiteSpace(underlying) ||
            sessionLow <= 0m || sessionHigh < sessionLow || padding < 0m)
            throw new ArgumentException("A valid market, session range and padding are required.");

        var prefix = $"{exchange}-{underlying}-{expiry.ToString("ddMMMyy", CultureInfo.InvariantCulture)}-";
        return growwContracts.Where(value => value.StartsWith(prefix,
                StringComparison.OrdinalIgnoreCase))
            .Where(value => TryStrike(value[prefix.Length..], out var strike) &&
                strike >= sessionLow - padding && strike <= sessionHigh + padding)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool TryStrike(string suffix, out decimal strike)
    {
        strike = 0m;
        var delimiter = suffix.LastIndexOf('-');
        if (delimiter <= 0 || delimiter == suffix.Length - 1) return false;
        var side = suffix[(delimiter + 1)..];
        return side is "CE" or "PE" &&
            decimal.TryParse(suffix[..delimiter], NumberStyles.None,
                CultureInfo.InvariantCulture, out strike) && strike > 0m;
    }
}
