using System.Security.Cryptography;
using System.Text;
using System.Globalization;

namespace TradingSystem.Application.Execution;

public sealed record HistoricalUniverseIndexRangePoint(DateTimeOffset OpenTimeUtc,
    decimal Low, decimal High);

public static class HistoricalUniverseFingerprint
{
    // Includes the complete sorted contract corridor and the underlying
    // high/low inputs that selected it. A later provider or data revision
    // cannot silently certify a page from a different universe.
    public static string Compute(DateOnly date, string market, DateOnly expiry,
        IReadOnlyList<string> symbols,
        IReadOnlyList<HistoricalUniverseIndexRangePoint> indexRange)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        ArgumentNullException.ThrowIfNull(indexRange);
        if (date == default || expiry < date || string.IsNullOrWhiteSpace(market) ||
            symbols.Any(string.IsNullOrWhiteSpace) ||
            symbols.Distinct(StringComparer.Ordinal).Count() != symbols.Count ||
            indexRange.Count == 0 ||
            indexRange.Select(value => value.OpenTimeUtc.ToUniversalTime())
                .Distinct().Count() != indexRange.Count)
            throw new ArgumentException("Verified universe inputs are required.");
        var input = new StringBuilder("historical-option-universe-v1\n")
            .Append(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            .Append('|').Append(market).Append('|')
            .Append(expiry.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            .Append('\n');
        foreach (var point in indexRange.OrderBy(value => value.OpenTimeUtc))
            input.Append(FormattableString.Invariant(
                $"I|{point.OpenTimeUtc.ToUniversalTime():O}|{point.Low}|{point.High}\n"));
        foreach (var symbol in symbols.OrderBy(value => value,
                     StringComparer.Ordinal))
            input.Append("S|").Append(symbol).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            input.ToString())));
    }
}
