using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using TradingSystem.Application.Broker;
using TradingSystem.Domain.Trading;

namespace TradingSystem.Infrastructure.MarketData;

// Research-only, missing-minute fallback. Never populates live trading candles.
internal sealed class UpstoxHistoricalCandleBackup(IHttpClientFactory clients, IConfiguration configuration) : IDisposable
{
    internal const string IndexSource = "UpstoxHistoricalIndexV3";
    internal const string FutureSource = "UpstoxHistoricalFutureV3";
    private readonly SemaphoreSlim catalogGate = new(1, 1);
    private DateOnly? catalogDate;
    private List<JsonElement> catalog = [];
    public void Dispose() => catalogGate.Dispose();
    public bool Enabled => !string.IsNullOrWhiteSpace(configuration["ReplayBackup:UpstoxAnalyticsToken"]);

    public async Task<GrowwHistoricalCandles> ReadAsync(Instrument instrument, string market, DateOnly date, CancellationToken token)
    {
        using var client = clients.CreateClient(UpstoxReplayBackupCapture.ClientName);
        await catalogGate.WaitAsync(token);
        string key;
        try
        {
            if (catalogDate != date)
            {
                using var response = await client.GetAsync("https://assets.upstox.com/market-quote/instruments/exchange/complete.json.gz",
                    HttpCompletionOption.ResponseHeadersRead, token);
                response.EnsureSuccessStatusCode();
                using var gzip = new GZipStream(await response.Content.ReadAsStreamAsync(token), CompressionMode.Decompress);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(30));
                var rows = new List<JsonElement>();
                await foreach (var row in JsonSerializer.DeserializeAsyncEnumerable<JsonElement>(gzip, cancellationToken: deadline.Token))
                    if (Text(row, "instrument_type") is "INDEX" or "FUT" &&
                        (Text(row, "segment") is "NSE_INDEX" or "BSE_INDEX" || Text(row, "underlying_symbol") is "NIFTY" or "SENSEX")) rows.Add(row.Clone());
                catalog = rows; catalogDate = date;
            }
            var matches = catalog.Where(row => Matches(row, instrument, market)).ToArray();
            if (matches.Length != 1) throw new InvalidDataException("Unique verified Upstox candle instrument is unavailable.");
            key = Text(matches[0], "instrument_key")!;
        }
        finally { catalogGate.Release(); }
        var day = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.upstox.com/v3/historical-candle/{Uri.EscapeDataString(key)}/minutes/1/{day}/{day}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", configuration["ReplayBackup:UpstoxAnalyticsToken"]);
        using var result = await client.SendAsync(request, token);
        result.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await result.Content.ReadAsStreamAsync(token));
        return Parse(json.RootElement, date);
    }

    internal static bool Matches(JsonElement row, Instrument instrument, string market)
    {
        if (instrument.Type == InstrumentType.Index)
        {
            var name = market switch { "NIFTY" => "Nifty 50", "BANKNIFTY" => "Nifty Bank", "SENSEX" => "SENSEX", _ => "" };
            return name.Length > 0 && instrument.TradingSymbol == market && Text(row, "instrument_type") == "INDEX" &&
                Text(row, "segment") == instrument.Exchange + "_INDEX" && Text(row, "instrument_key") == instrument.Exchange + "_INDEX|" + name;
        }
        return instrument.Type == InstrumentType.Future && instrument.ExpiryDate is { } expiry &&
            Text(row, "segment") == instrument.Exchange + "_FO" && Text(row, "instrument_type") == "FUT" &&
            Text(row, "underlying_symbol") == market && Text(row, "instrument_key") == instrument.Exchange + "_FO|" + instrument.ExchangeToken &&
            row.TryGetProperty("lot_size", out var lot) && lot.TryGetInt32(out var size) && size == instrument.LotSize &&
            row.TryGetProperty("expiry", out var exp) && exp.TryGetInt64(out var at) && at is >= 1_000_000_000_000 and <= 4_000_000_000_000 &&
            DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(at).ToOffset(TimeSpan.FromHours(5.5)).DateTime) == expiry;
    }

    internal static GrowwHistoricalCandles Parse(JsonElement root, DateOnly date)
    {
        if (Text(root, "status") != "success" || !root.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("candles", out var candles) || candles.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Upstox candle response is invalid.");
        var result = new List<GrowwHistoricalCandle>();
        foreach (var row in candles.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 6 ||
                !DateTimeOffset.TryParse(row[0].GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ||
                at.Second != 0 || at.Ticks % TimeSpan.TicksPerMinute != 0)
                throw new InvalidDataException("Upstox minute timestamp is invalid.");
            var local = at.ToOffset(TimeSpan.FromHours(5.5));
            if (DateOnly.FromDateTime(local.DateTime) != date || local.TimeOfDay < new TimeSpan(9, 15, 0) || local.TimeOfDay >= new TimeSpan(15, 30, 0)) continue;
            var open = row[1].GetDecimal(); var high = row[2].GetDecimal(); var low = row[3].GetDecimal(); var close = row[4].GetDecimal();
            var volume = row[5].GetInt64(); decimal? oi = row.GetArrayLength() > 6 && row[6].ValueKind != JsonValueKind.Null ? row[6].GetDecimal() : null;
            if (open <= 0 || close <= 0 || low <= 0 || high < Math.Max(open, close) || low > Math.Min(open, close) || volume < 0 || oi < 0)
                throw new InvalidDataException("Upstox candle values are invalid.");
            result.Add(new(local.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture), open, high, low, close, volume, oi));
        }
        return new(result, result.LastOrDefault()?.Close ?? 0m, 1);
    }

    private static string? Text(JsonElement row, string key) => row.ValueKind == JsonValueKind.Object &&
        row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
