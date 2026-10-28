using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TradingSystem.Application.MarketData;

namespace TradingSystem.Infrastructure.MarketData;

// Optional, read-only context for the morning brief. It never participates in trade decisions.
internal sealed partial class UpstoxPreSessionSource(HttpClient http, IConfiguration configuration,
    ILogger<UpstoxPreSessionSource> logger)
{
    private const string InstrumentUrl =
        "https://assets.upstox.com/market-quote/instruments/exchange/global.json.gz";
    private static readonly string[] WantedNames =
    [
        "GIFT NIFTY", "DOW JONES", "S&P 500", "S&P", "HANG SENG",
        "NIKKEI 225", "US TECH 100", "OIL (BRENT)", "OIL (WTI)"
    ];

    internal async Task<IReadOnlyList<PreSessionEvidence>> ReadAsync(DateOnly sessionDate,
        DateTimeOffset capturedAtUtc, CancellationToken cancellationToken)
    {
        var token = configuration["Upstox:AnalyticsToken"];
        if (string.IsNullOrWhiteSpace(token)) return [];

        var evidence = new List<PreSessionEvidence>();
        try
        {
            var instruments = await ReadInstrumentsAsync(cancellationToken);
            if (instruments.Count > 0)
            {
                var keys = string.Join(',', instruments.Values.Select(value => value.Key).Distinct());
                var url = "https://api.upstox.com/v2/market-quote/quotes?instrument_key=" +
                    Uri.EscapeDataString(keys);
                var json = await GetJsonAsync(url, token, cancellationToken);
                evidence.AddRange(UpstoxPreSessionParser.ParseQuotes(json, instruments,
                    capturedAtUtc));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            LogQuotesUnavailable(logger);
        }

        var fiiUrl = "https://api.upstox.com/v2/market/fii?data_type=NSE_EQ%7CCASH&interval=1D";
        var diiUrl = "https://api.upstox.com/v2/market/dii?data_type=NSE_EQ%7CCASH&interval=1D";
        var fiiTask = GetFlowJsonAsync(fiiUrl, token, cancellationToken);
        var diiTask = GetFlowJsonAsync(diiUrl, token, cancellationToken);
        await Task.WhenAll(fiiTask, diiTask);
        try
        {
            var flows = UpstoxPreSessionParser.ParseFlows(fiiTask.Result, diiTask.Result,
                sessionDate);
            if (flows is not null) evidence.Add(flows);
        }
        catch (JsonException) { LogFlowsUnavailable(logger); }
        return evidence;
    }

    private async Task<string?> GetFlowJsonAsync(string url, string token,
        CancellationToken cancellationToken)
    {
        try { return await GetJsonAsync(url, token, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            LogFlowsUnavailable(logger);
            return null;
        }
    }

    private async Task<Dictionary<string, UpstoxInstrument>> ReadInstrumentsAsync(
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        using var response = await http.GetAsync(InstrumentUrl,
            HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 2_000_000)
            throw new InvalidDataException("Instrument file too large");
        await using var raw = await response.Content.ReadAsStreamAsync(timeout.Token);
        await using var gzip = new GZipStream(raw, CompressionMode.Decompress);
        var bytes = await ReadLimitedAsync(gzip, 5_000_000, timeout.Token);
        return UpstoxPreSessionParser.ParseInstruments(bytes, WantedNames);
    }

    private async Task<string> GetJsonAsync(string url, string token,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await http.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 1_000_000)
            throw new InvalidDataException("Upstox response too large");
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        var bytes = await ReadLimitedAsync(stream, 1_000_000, timeout.Token);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    private static async Task<byte[]> ReadLimitedAsync(Stream stream, int limit,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[16_384];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0) return output.ToArray();
            if (output.Length + read > limit) throw new InvalidDataException("Feed exceeds size limit");
            output.Write(buffer, 0, read);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Upstox pre-session global quotes unavailable")]
    private static partial void LogQuotesUnavailable(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Upstox pre-session institutional flows unavailable")]
    private static partial void LogFlowsUnavailable(ILogger logger);
}

internal sealed record UpstoxInstrument(string Key, string Name, int LatencySeconds);

internal static class UpstoxPreSessionParser
{
    private static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);

    internal static Dictionary<string, UpstoxInstrument> ParseInstruments(
        ReadOnlySpan<byte> bytes, IReadOnlyCollection<string> wantedNames)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            bytes = bytes[3..];
        using var document = JsonDocument.Parse(bytes.ToArray());
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new JsonException("Expected instrument array");
        var result = new Dictionary<string, UpstoxInstrument>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var name = GetString(item, "name");
            var key = GetString(item, "instrument_key");
            if (name is null || key is null || !wantedNames.Contains(name,
                    StringComparer.OrdinalIgnoreCase) ||
                !(key.StartsWith("GLOBAL_INDEX|", StringComparison.Ordinal) ||
                  key.StartsWith("GLOBAL_INDICATOR|", StringComparison.Ordinal))) continue;
            var latency = GetString(item, "latency")?.Split(' ')[0];
            result[name] = new UpstoxInstrument(key, name.ToUpperInvariant(),
                int.TryParse(latency, CultureInfo.InvariantCulture, out var seconds) ? seconds : 900);
        }
        return result;
    }

    internal static IReadOnlyList<PreSessionEvidence> ParseQuotes(string json,
        IReadOnlyDictionary<string, UpstoxInstrument> instruments, DateTimeOffset capturedAtUtc)
    {
        using var document = JsonDocument.Parse(json);
        if (GetString(document.RootElement, "status") != "success" ||
            !TryProperty(document.RootElement, "data", out var data) ||
            data.ValueKind != JsonValueKind.Object) return [];
        var quotes = new Dictionary<string, (decimal Price, DateTimeOffset At, decimal? Change)>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var row in data.EnumerateObject())
        {
            var value = row.Value;
            var key = GetString(value, "instrument_token");
            if (key is null) continue;
            var instrument = instruments.Values.FirstOrDefault(candidate => candidate.Key == key);
            if (instrument is null || !GetDecimal(value, "last_price", out var price) || price <= 0)
                continue;
            var name = instrument.Name.ToUpperInvariant();
            var epoch = GetLong(value, "last_trade_time") ?? GetLong(value, "timestamp");
            if (epoch is null || epoch <= 0) continue;
            DateTimeOffset at;
            try { at = DateTimeOffset.FromUnixTimeMilliseconds(epoch.Value); }
            catch (ArgumentOutOfRangeException) { continue; }
            var age = capturedAtUtc - at;
            var maxAge = name == "GIFT NIFTY" ? TimeSpan.FromMinutes(10) :
                name.StartsWith("OIL (", StringComparison.Ordinal) ?
                    TimeSpan.FromMinutes(5) : TimeSpan.FromHours(18);
            if (age < TimeSpan.FromMinutes(-1) || age > maxAge) continue;
            quotes[name] = (price, at,
                GetDecimal(value, "net_change", out var change) ? change : null);
        }

        var evidence = new List<PreSessionEvidence>();
        if (quotes.TryGetValue("GIFT NIFTY", out var gift))
            evidence.Add(new("GIFT Nifty", "TimestampedQuote", Format(gift),
                "Upstox global market quote", gift.At,
                "Provider instrument refresh is 120 seconds; not an NSE IX direct feed or an opening prediction."));
        var world = quotes.Where(value => value.Key is "DOW JONES" or "S&P 500" or "S&P" or
                "HANG SENG" or "NIKKEI 225" or "US TECH 100")
            .OrderBy(value => value.Key).ToArray();
        if (world.Length > 0)
            evidence.Add(new("US and Asian markets", "TimestampedQuote",
                string.Join("; ", world.Select(value => $"{value.Key} {Format(value.Value)}")),
                "Upstox global market quotes", world.Max(value => value.Value.At),
                "Each value carries its own IST time; closed-market values are prior-session context."));
        var crude = quotes.Where(value => value.Key is "OIL (BRENT)" or "OIL (WTI)")
            .OrderBy(value => value.Key).ToArray();
        if (crude.Length > 0)
            evidence.Add(new("Crude oil", "TimestampedQuote",
                string.Join("; ", crude.Select(value => $"{value.Key} {Format(value.Value)}")),
                "Upstox global indicators", crude.Max(value => value.Value.At),
                "Timestamped provider quote; check market hours and freshness."));
        return evidence;
    }

    internal static PreSessionEvidence? ParseFlows(string? fiiJson, string? diiJson,
        DateOnly sessionDate)
    {
        var fii = ParseFlow(fiiJson, sessionDate);
        var dii = ParseFlow(diiJson, sessionDate);
        if (fii is null && dii is null) return null;
        var parts = new List<string>();
        if (fii is { } f) parts.Add($"FII {f.Day:yyyy-MM-dd} net ₹{f.Net:+0.##;-0.##;0}");
        if (dii is { } d) parts.Add($"DII {d.Day:yyyy-MM-dd} net ₹{d.Net:+0.##;-0.##;0}");
        return new("FII/DII prior-session flows", "PreviousSession",
            string.Join("; ", parts), "Upstox NSE cash institutional activity", null,
            "Daily buy minus sell as reported by provider; unavailable side is omitted. Not today's flow.");
    }

    private static (DateOnly Day, decimal Net)? ParseFlow(string? json, DateOnly sessionDate)
    {
        if (string.IsNullOrEmpty(json)) return null;
        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException) { return null; }
        using (document)
        {
        if (GetString(document.RootElement, "status") != "success" ||
            !TryProperty(document.RootElement, "data", out var data) ||
            !TryProperty(data, "NSE_EQ|CASH", out var rows) ||
            rows.ValueKind != JsonValueKind.Array) return null;
        var found = new List<(DateOnly Day, decimal Net)>();
        foreach (var row in rows.EnumerateArray())
        {
            var epoch = GetLong(row, "time_stamp");
            if (epoch is null || !GetDecimal(row, "buy_amount", out var buy) ||
                !GetDecimal(row, "sell_amount", out var sell)) continue;
            DateOnly day;
            try { day = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(epoch.Value)
                .ToOffset(IstOffset).Date); }
            catch (ArgumentOutOfRangeException) { continue; }
            if (day < sessionDate && day >= sessionDate.AddDays(-5))
                found.Add((day, buy - sell));
        }
        return found.OrderByDescending(value => value.Day).FirstOrDefault() is var latest &&
            latest.Day != default ? latest : null;
        }
    }

    private static string Format((decimal Price, DateTimeOffset At, decimal? Change) quote) =>
        $"{quote.Price:0.##}{(quote.Change is { } change ? $" ({change:+0.##;-0.##;0})" : "")} " +
        $"@ {quote.At.ToOffset(IstOffset):dd MMM HH:mm} IST";

    private static bool TryProperty(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value);
    }

    private static string? GetString(JsonElement element, string name) =>
        TryProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String ?
            value.GetString() : null;

    private static long? GetLong(JsonElement element, string name) =>
        TryProperty(element, name, out var value) &&
        ((value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _)) ||
         (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out _))) &&
        long.TryParse(value.ToString(), CultureInfo.InvariantCulture, out var number) ? number : null;

    private static bool GetDecimal(JsonElement element, string name, out decimal number)
    {
        number = 0;
        return TryProperty(element, name, out var value) &&
            decimal.TryParse(value.ToString(), NumberStyles.Number,
                CultureInfo.InvariantCulture, out number);
    }
}
