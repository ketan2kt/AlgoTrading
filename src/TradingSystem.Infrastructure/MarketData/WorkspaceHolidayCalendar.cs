using System.Text.Json;
using System.Globalization;

namespace TradingSystem.Infrastructure.MarketData;

// Display-only calendar. Never used to arm trading, alter recording, or gate orders.
// The public endpoint needs no broker token and is shared across all chart requests.
internal sealed class WorkspaceHolidayCalendar(IHttpClientFactory clients, TimeProvider clock) : IDisposable
{
    internal const string ClientName = "WorkspaceHolidayCalendar";
    private readonly SemaphoreSlim refresh = new(1, 1);
    private readonly Dictionary<string, (DateTimeOffset RetryAt, Dictionary<DateOnly, HashSet<string>> Days)> cache = [];

    internal async Task<HashSet<DateOnly>> ClosedDatesAsync(string exchange, DateOnly from, DateOnly through,
        CancellationToken cancellationToken)
    {
        var currentYear = DateOnly.FromDateTime(clock.GetUtcNow().ToOffset(TimeSpan.FromHours(5.5)).DateTime).Year;
        var result = new HashSet<DateOnly>();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            Add(await ReadAsync($"year:{currentYear}", "", budget.Token));
            // On a January restart the current-year list cannot describe December.
            // Date-specific lookups are cached too, rather than guessing across years.
            for (var date = from; date <= through; date = date.AddDays(1))
                if (date.Year != currentYear)
                    Add(await ReadAsync(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        "/" + date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), budget.Token));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { /* Calendar lookup must not stall chart loading across a year boundary. */ }
        return result;

        void Add(Dictionary<DateOnly, HashSet<string>> days)
        {
            foreach (var (date, exchanges) in days)
                if (date >= from && date <= through && exchanges.Contains(exchange)) result.Add(date);
        }
    }

    private async Task<Dictionary<DateOnly, HashSet<string>>> ReadAsync(string key, string suffix,
        CancellationToken cancellationToken)
    {
        await refresh.WaitAsync(cancellationToken);
        try
        {
            var now = clock.GetUtcNow();
            cache.TryGetValue(key, out var prior);
            if (prior.RetryAt > now) return prior.Days;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                using var client = clients.CreateClient(ClientName);
                using var response = await client.GetAsync(
                    "https://api.upstox.com/v2/market/holidays" + suffix, timeout.Token);
                response.EnsureSuccessStatusCode();
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
                var days = Parse(json.RootElement);
                cache[key] = (now.AddHours(12), days);
                return days;
            }
            catch (Exception error) when (error is HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException ||
                                          error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                // Keep verified dates during a calendar outage. Unknown days remain
                // eligible for the strict final-candle check; never invent a close.
                var days = prior.Days ?? [];
                cache[key] = (now.AddMinutes(5), days);
                return days;
            }
        }
        finally { refresh.Release(); }
    }

    internal static Dictionary<DateOnly, HashSet<string>> Parse(JsonElement root)
    {
        if (root.GetProperty("status").GetString() != "success") throw new JsonException("Calendar unavailable.");
        var result = new Dictionary<DateOnly, HashSet<string>>();
        foreach (var item in root.GetProperty("data").EnumerateArray())
        {
            if (item.GetProperty("holiday_type").GetString() != "TRADING_HOLIDAY") continue;
            if (!DateOnly.TryParseExact(item.GetProperty("date").GetString(), "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) continue;
            var closed = item.GetProperty("closed_exchanges").EnumerateArray()
                .Select(value => value.GetString()).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
            // Special sessions/open exchanges take precedence over a generic holiday label.
            foreach (var open in item.GetProperty("open_exchanges").EnumerateArray())
                if (open.TryGetProperty("exchange", out var name) && name.GetString() is { } value) closed.Remove(value);
            result[date] = closed;
        }
        return result;
    }

    public void Dispose() => refresh.Dispose();
}
