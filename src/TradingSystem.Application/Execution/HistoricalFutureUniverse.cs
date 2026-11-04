using System.Globalization;

namespace TradingSystem.Application.Execution;

public static class HistoricalFutureUniverse
{
    public static string? SelectForExpiry(IReadOnlyList<string> growwContracts,
        string exchange, string underlying, DateOnly expiryDate)
    {
        ArgumentNullException.ThrowIfNull(growwContracts);
        if (string.IsNullOrWhiteSpace(exchange) || string.IsNullOrWhiteSpace(underlying))
            throw new ArgumentException("Exchange and underlying are required.");
        var symbol = $"{exchange}-{underlying}-{expiryDate.ToString("ddMMMyy",
            CultureInfo.InvariantCulture)}-FUT";
        return growwContracts.SingleOrDefault(value => string.Equals(value,
            symbol, StringComparison.OrdinalIgnoreCase));
    }
}
