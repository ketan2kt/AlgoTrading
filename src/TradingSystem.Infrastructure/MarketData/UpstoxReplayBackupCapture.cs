using System.Globalization;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TradingSystem.Application.Broker;
using TradingSystem.Application.Execution;
using TradingSystem.Domain.Trading;

namespace TradingSystem.Infrastructure.MarketData;

// Read-only capture on the configured host. Uses the exact dated contract plan; no order services.
internal sealed class UpstoxReplayBackupCapture(IHttpClientFactory clients, IConfiguration configuration,
    IHistoricalQuoteArchive archive, TimeProvider clock, ILogger<UpstoxReplayBackupCapture> logger,
    WorkspaceChartCache? chartCache = null, UpstoxPaperQuoteCache? paperQuotes = null) : IDisposable
{
    private readonly SemaphoreSlim catalogGate = new(1, 1);
    private DateOnly? catalogDate;
    private IReadOnlyDictionary<string, Instrument?> catalog = new Dictionary<string, Instrument?>();
    public void Dispose() => catalogGate.Dispose();
    internal const string ClientName = "UpstoxReplayBackup";
    internal const string CatalogClientName = "UpstoxReplayCatalog";
    internal static readonly TimeSpan MaximumAge = TimeSpan.FromSeconds(30);
    private static readonly Action<ILogger, string, Exception?> Unavailable = LoggerMessage.Define<string>(
        LogLevel.Warning, new EventId(6755, "UpstoxReplayBackupUnavailable"), "Upstox replay backup unavailable ({FailureType}). Primary capture is unaffected.");
    private static readonly Action<ILogger, string, double, int, Exception?> CatalogStage = LoggerMessage.Define<string, double, int>(
        LogLevel.Information, new EventId(6756, "UpstoxCatalogStage"), "Upstox catalog {Stage}: {ElapsedMilliseconds} ms, {Rows} rows.");
    private readonly string? credential = configuration["ReplayBackup:UpstoxAnalyticsToken"];
    public bool Enabled => !string.IsNullOrWhiteSpace(credential) &&
        (configuration["ReplayBackup:Host"] != "Website" || !ReplayRecorderHosting.IsRecorder(configuration));

    public async Task RunAsync(IReadOnlyList<HistoricalFeedInstrument> plan, GrowwHistoricalFeedAccumulator primary,
        DateTimeOffset open, DateTimeOffset close, CancellationToken token, bool persistAll = false)
    {
        // Website fallback may run primary capture too. Its embedded supplement
        // must not compete with the separately leased independent collector.
        if (!Enabled || (!persistAll && configuration["ReplayBackup:Host"] == "Website")) return;
        var date = DateOnly.FromDateTime(open.ToOffset(TimeSpan.FromHours(5.5)).DateTime);
        archive.SetBackupCredentialExpiry(date, CredentialExpiry(credential));
        if (plan.Count > 500 || plan.Any(item => item.Market is not ("NIFTY" or "SENSEX" or "BANKNIFTY")))
            throw new InvalidDataException("Backup plan exceeds the supported three-market budget.");
        Dictionary<string, HistoricalFeedInstrument>? mapped = null;
        PublishCoverage(new Dictionary<string, HistoricalFeedInstrument>());
        using var client = clients.CreateClient(ClientName);
        using var catalogClient = clients.CreateClient(CatalogClientName);
        while (mapped is null && !token.IsCancellationRequested && clock.GetUtcNow() < close)
        {
            try
            {
                mapped = await LoadPlanAsync(catalogClient, plan, date, token);
                PublishCoverage(mapped);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                foreach (var market in plan.Select(item => item.Market).Distinct()) Report(market, "Instrument catalog: " + FailureDescription(error));
                await Task.Delay(TimeSpan.FromSeconds(15), clock, token);
            }
        }
        if (mapped is null) return;
        // Warm, independent per-market capture. At most three requests every
        // three seconds (1,800/30 minutes), each <=500 keys. No order API calls.
        // One slow/failed market never serializes the other market's samples.
        await Task.WhenAll(mapped.GroupBy(pair => pair.Value.Market)
            .Select(group => CaptureMarketAsync(group.Key, group.ToDictionary(pair => pair.Key, pair => pair.Value))));

        void PublishCoverage(IReadOnlyDictionary<string, HistoricalFeedInstrument> matches) => archive.SetBackupCoverage(date,
            plan.Where(item => item.Type is InstrumentType.Index or InstrumentType.CallOption or InstrumentType.PutOption).GroupBy(item => item.Market)
                .ToDictionary(group => group.Key, group =>
                {
                    var missing = group.Where(item => !matches.ContainsKey(item.Type == InstrumentType.Index ? IndexKey(item.Market) ?? "" : item.Exchange + "_FO|" + item.ExchangeToken))
                        .Select(item => item.TradingSymbol).Order(StringComparer.Ordinal).ToArray();
                    return new HistoricalBackupMarketCoverage(group.Count(), group.Count() - missing.Length, missing);
                }));

        void Report(string market, string reason)
        {
            archive.SetBackupFailure(date, market, reason);
            Unavailable(logger, market + ": " + reason, null);
        }

        async Task CaptureMarketAsync(string market, Dictionary<string, HistoricalFeedInstrument> instruments)
        {
            var recorded = new Dictionary<string, ((decimal Bid, long BidSize, decimal Ask, long AskSize) Depth,
                DateTimeOffset LiquidityAt, DateTimeOffset ObservedAt)>(StringComparer.Ordinal);
            // Keep a bounded warm window while primary capture is healthy. On
            // a silent failure, persist the already observed lead-in as well,
            // rather than begin recording only after the 30-second detector.
            var warm = instruments.Keys.ToDictionary(key => key, _ => new Queue<HistoricalQuoteObservation>(), StringComparer.Ordinal);
            var recovering = new HashSet<string>(StringComparer.Ordinal);
            var batch = instruments.Keys.Order(StringComparer.Ordinal).ToArray();
            while (!token.IsCancellationRequested && clock.GetUtcNow() < close)
            {
                // Leave a bounded quote budget for paper confirmation futures.
                var delay = TimeSpan.FromSeconds(paperQuotes is null ? 3 : 4);
                var preOpen = clock.GetUtcNow() < open;
                try
                {
                    var uri = "https://api.upstox.com/v3/market-quote/quotes?instrument_key=" + Uri.EscapeDataString(string.Join(',', batch));
                    using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
                    using var response = await client.SendAsync(request, token);
                    if ((int)response.StatusCode == 429)
                    {
                        delay = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - clock.GetUtcNow()) ?? TimeSpan.FromSeconds(15);
                        if (delay < TimeSpan.FromSeconds(3)) delay = TimeSpan.FromSeconds(3);
                        Report(market, "HTTP 429: broker retry delay");
                        await Task.Delay(delay, clock, token);
                        continue;
                    }
                    if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                        delay = TimeSpan.FromMinutes(1);
                    response.EnsureSuccessStatusCode();
                    using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(token));
                    var received = clock.GetUtcNow();
                    if (received >= close) return;
                    if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                        throw new InvalidDataException("Upstox quote response omitted data.");
                    if (preOpen && received < open)
                    {
                        // Verify authorization and contract availability before
                        // opening, without calling yesterday's books fresh.
                        var available = data.EnumerateObject().Select(item => String(item.Value, "instrument_token"))
                            .Where(key => key is not null).ToHashSet(StringComparer.Ordinal);
                        archive.SetBackupFailure(date, market, batch.All(available.Contains) ? null : "Pre-open quote contracts missing");
                        await Task.Delay(TimeSpan.FromTicks(Math.Min(TimeSpan.FromMinutes(1).Ticks, (open - received).Ticks)), clock, token);
                        continue;
                    }
                    var accepted = new HashSet<string>(StringComparer.Ordinal);
                    var refused = false;
                    foreach (var element in data.EnumerateObject())
                    {
                        var identity = String(element.Value, "instrument_token");
                        if (identity is null || !instruments.TryGetValue(identity, out var instrument)) continue;
                        if (instrument.Type == InstrumentType.Index)
                        {
                            if (!TryReadIndex(element.Value, received, out var indexQuote, out var indexAt)) continue;
                            chartCache?.Observe(market, indexQuote.LastPrice, indexAt);
                            paperQuotes?.Observe(identity, indexQuote, indexAt);
                            accepted.Add(identity);
                            if (persistAll || primary.NeedsBackup(instrument, received))
                                if (!archive.TryRecordBackup(new(Guid.NewGuid(), received,
                                    new(instrument.Exchange, instrument.Segment, instrument.TradingSymbol), indexQuote)
                                    { Source = HistoricalQuoteObservation.UpstoxSnapshot, BrokerObservedAtUtc = indexAt })) refused = true;
                            continue;
                        }
                        if (!TryRead(element.Value, received, out var key, out var quote, out var observedAt)) continue;
                        paperQuotes?.Observe(key, quote, observedAt);
                        accepted.Add(key);
                        var depth = (quote.BidPrice!.Value, quote.BidQuantity!.Value, quote.OfferPrice!.Value, quote.OfferQuantity!.Value);
                        var hasPrior = recorded.TryGetValue(key, out var prior);
                        if (hasPrior && observedAt <= prior.ObservedAt) continue;
                        var liquidityAt = hasPrior && prior.Depth == depth ? prior.LiquidityAt : observedAt;
                        // Keep every fresh observation (including changed LTP on
                        // an unchanged book), but do not invent replenished size.
                        warm[key].Enqueue(new(Guid.NewGuid(), received,
                                new(instrument.Exchange, instrument.Segment, instrument.TradingSymbol), quote)
                            { Source = HistoricalQuoteObservation.UpstoxSnapshot, BrokerObservedAtUtc = observedAt,
                                BackupLiquidityAtUtc = liquidityAt });
                        recorded[key] = (depth, liquidityAt, observedAt);
                    }
                    foreach (var key in batch)
                    {
                        var buffer = warm[key];
                        if (persistAll || primary.NeedsBackup(instruments[key], received)) recovering.Add(key);
                        if (recovering.Contains(key))
                        {
                            while (buffer.TryPeek(out var sample))
                            {
                                if (!archive.TryRecordBackup(sample)) { refused = true; break; }
                                buffer.Dequeue();
                            }
                            if (buffer.Count == 0) recovering.Remove(key);
                        }
                        while (buffer.TryPeek(out var old) && (received - old.ReceivedAtUtc > TimeSpan.FromSeconds(45) || buffer.Count > 20))
                        {
                            buffer.Dequeue();
                            if (recovering.Contains(key)) refused = true;
                        }
                    }
                    archive.SetBackupFailure(date, market, refused ? "Backup archive rejected observations" :
                        accepted.Count != batch.Length ? "Fresh quotes missing: " + string.Join(", ", batch.Except(accepted).Select(key => instruments[key].TradingSymbol)) : null);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch (Exception error)
                {
                    Report(market, FailureDescription(error));
                    if (delay < TimeSpan.FromSeconds(15)) delay = TimeSpan.FromSeconds(15);
                }
                try { await Task.Delay(delay, clock, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            }
        }
    }

    // Metadata is advisory only: the actual quote request remains the auth check.
    // Never expose or persist the token/payload. Opaque tokens remain supported.
    internal static DateTimeOffset? CredentialExpiry(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 16384) return null;
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 3) return null;
            var encoded = parts[1].Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight(encoded.Length + (4 - encoded.Length % 4) % 4, '=');
            using var payload = JsonDocument.Parse(Convert.FromBase64String(encoded));
            return payload.RootElement.TryGetProperty("exp", out var expiry) && expiry.TryGetInt64(out var seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
        }
        catch (Exception error) when (error is FormatException or JsonException or ArgumentOutOfRangeException) { return null; }
    }

    // Only structured error codes: never persist response bodies, credentials or URLs.
    internal static string FailureDescription(Exception error) => error switch
    {
        HttpRequestException { StatusCode: { } status } => "HTTP " + (int)status,
        HttpRequestException http => "Transport " + http.HttpRequestError,
        _ => error.GetType().Name
    };

    internal async Task<Dictionary<string, HistoricalFeedInstrument>> LoadPlanAsync(HttpClient client,
        IReadOnlyList<HistoricalFeedInstrument> plan, DateOnly date, CancellationToken token)
    {
        await catalogGate.WaitAsync(token);
        try
        {
            if (catalogDate != date)
            {
                // Publish only a completely parsed, dated catalogue. A lease
                // reacquisition reuses metadata, never old quotes or mappings.
                var loaded = await LoadCatalogAsync(client, token);
                catalog = loaded;
                catalogDate = date;
            }
            return MapPlan(plan, catalog);
        }
        finally { catalogGate.Release(); }
    }

    private static Dictionary<string, HistoricalFeedInstrument> MapPlan(
        IReadOnlyList<HistoricalFeedInstrument> plan, IReadOnlyDictionary<string, Instrument?> metadata)
    {
        var mapped = new Dictionary<string, HistoricalFeedInstrument>(StringComparer.Ordinal);
        foreach (var item in plan)
        {
            var key = item.Type == InstrumentType.Index ? IndexKey(item.Market) : item.Exchange + "_FO|" + item.ExchangeToken;
            if (key is null || !metadata.TryGetValue(key, out var row) || row is null || row.Key != key) continue;
            if (item.Type == InstrumentType.Index
                ? row.Side == "INDEX" && row.Segment == item.Exchange + "_INDEX"
                : item.Type is InstrumentType.CallOption or InstrumentType.PutOption &&
                  row.Strike is { } strike && row.Lot is { } lot && row.Expiry is { } expiry &&
                  Matches(item, row.Underlying, row.Side, strike, lot, expiry))
                mapped.Add(key, item);
        }
        return mapped;
    }

    private async Task<IReadOnlyDictionary<string, Instrument?>> LoadCatalogAsync(HttpClient client, CancellationToken token)
    {
        var mapped = new Dictionary<string, Instrument?>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var timer = Stopwatch.StartNew();
        // Buffer the bounded compressed document before parsing. Streaming tiny
        // decompressor reads directly from the CDN ties metadata parsing to the
        // network connection and makes startup/restart recovery unpredictable.
        using var response = await client.GetAsync("https://assets.upstox.com/market-quote/instruments/exchange/complete.json.gz",
            HttpCompletionOption.ResponseContentRead, token);
        response.EnsureSuccessStatusCode();
        CatalogStage(logger, "downloaded", timer.Elapsed.TotalMilliseconds, 0, null);
        timer.Restart();
        using var gzip = new GZipStream(await response.Content.ReadAsStreamAsync(token), CompressionMode.Decompress);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        // Daily metadata is much larger than a quote response. Keep a bounded
        // processing budget without relaxing quote freshness or quote timeouts.
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        var rows = 0;
        using var document = await JsonDocument.ParseAsync(gzip, cancellationToken: deadline.Token);
        foreach (var element in document.RootElement.EnumerateArray())
        {
            deadline.Token.ThrowIfCancellationRequested();
            rows++;
            if (String(element, "segment") is not ("NSE_FO" or "BSE_FO" or "NSE_INDEX" or "BSE_INDEX")) continue;
            // Avoid allocating/deserializing the full stock/option universe into
            // typed rows; only the three supported indexes need validated metadata.
            var isIndex = String(element, "instrument_type") == "INDEX";
            var exchangeToken = element.TryGetProperty("exchange_token", out var tokenValue) ? tokenValue.ValueKind switch
            {
                JsonValueKind.String => tokenValue.GetString(),
                JsonValueKind.Number => tokenValue.GetRawText(),
                _ => null
            } : null;
            var identity = isIndex ? String(element, "instrument_key") : exchangeToken is null ? null : String(element, "segment") + "|" + exchangeToken;
            if (identity is null) continue;
            if (!seen.Add(identity)) { mapped[identity] = null; continue; }
            if (isIndex ? String(element, "instrument_key") is not ("NSE_INDEX|Nifty 50" or "NSE_INDEX|Nifty Bank" or "BSE_INDEX|SENSEX")
                : String(element, "underlying_symbol") is not ("NIFTY" or "SENSEX" or "BANKNIFTY")) continue;
            var row = element.Deserialize<Instrument>();
            if (row is null) continue;
            mapped.Add(identity, row);
        }
        CatalogStage(logger, "mapped", timer.Elapsed.TotalMilliseconds, rows, null);
        return mapped;
    }

    internal static string? IndexKey(string market) => market switch
    { "NIFTY" => "NSE_INDEX|Nifty 50", "BANKNIFTY" => "NSE_INDEX|Nifty Bank", "SENSEX" => "BSE_INDEX|SENSEX", _ => null };

    internal static bool TryReadIndex(JsonElement value, DateTimeOffset received, out GrowwQuote quote, out DateTimeOffset observedAt)
    {
        quote = null!; observedAt = default;
        if (value.ValueKind != JsonValueKind.Object ||
            !DateTimeOffset.TryParse(String(value, "timestamp"), CultureInfo.InvariantCulture, DateTimeStyles.None, out observedAt) ||
            observedAt > received || received - observedAt > MaximumAge || Number(value, "last_price") is not > 0m) return false;
        // Indices do not trade and have no executable book. Preserve the broker
        // observation clock separately; never fabricate exchange trade/depth time.
        quote = new(Number(value, "last_price")!.Value, null, null, null, null, null, null, null, null);
        return true;
    }

    internal static bool Matches(HistoricalFeedInstrument plan, string? underlying, string? side, decimal strike, int lot, long expiry) =>
        underlying == plan.Market && side == (plan.Type == InstrumentType.CallOption ? "CE" : "PE") &&
        strike == plan.StrikePrice && lot == plan.LotSize && expiry is >= 1_000_000_000_000 and <= 4_000_000_000_000 &&
        DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(expiry).ToOffset(TimeSpan.FromHours(5.5)).DateTime) == plan.ExpiryDate;

    internal static bool TryRead(JsonElement value, DateTimeOffset received, out string key, out GrowwQuote quote, out DateTimeOffset observedAt)
    {
        key = ""; quote = null!; observedAt = default;
        if (value.ValueKind != JsonValueKind.Object || String(value, "instrument_token") is not { Length: > 0 } id ||
            !DateTimeOffset.TryParse(String(value, "timestamp"), CultureInfo.InvariantCulture, DateTimeStyles.None, out observedAt) ||
            !long.TryParse(String(value, "last_trade_time"), CultureInfo.InvariantCulture, out var trade) ||
            trade < (received - MaximumAge).ToUnixTimeMilliseconds() || trade > observedAt.ToUnixTimeMilliseconds() ||
            observedAt > received || observedAt < received - MaximumAge || Number(value, "last_price") is not > 0m ||
            !value.TryGetProperty("depth", out var depth) || depth.ValueKind != JsonValueKind.Object) return false;
        var bid = BestSide(depth, "buy", true);
        var ask = BestSide(depth, "sell", false);
        if (bid is not { } b || ask is not { } a || a.Price < b.Price) return false;
        key = id;
        quote = new(Number(value, "last_price")!.Value, trade, b.Price, b.Size, a.Price, a.Size,
            Number(value, "volume"), Number(value, "oi"), null);
        return true;
    }

    private static (decimal Price, long Size)? BestSide(JsonElement depth, string property, bool highest)
    {
        if (!depth.TryGetProperty(property, out var side) || side.ValueKind != JsonValueKind.Array) return null;
        (decimal Price, long Size)? best = null;
        foreach (var level in side.EnumerateArray())
            if (level.ValueKind == JsonValueKind.Object && Number(level, "price") is > 0m and var price &&
                level.TryGetProperty("quantity", out var quantity) && quantity.ValueKind == JsonValueKind.Number &&
                quantity.TryGetInt64(out var units) && units > 0 && (best is null || (highest ? price > best.Value.Price : price < best.Value.Price)))
                best = (price, units);
        return best;
    }

    private static decimal? Number(JsonElement value, string name) => value.TryGetProperty(name, out var field) &&
        field.ValueKind == JsonValueKind.Number && field.TryGetDecimal(out var number) ? number : null;
    private static string? String(JsonElement value, string name) => value.TryGetProperty(name, out var field) &&
        field.ValueKind == JsonValueKind.String ? field.GetString() : null;

    private sealed record Instrument([property: JsonPropertyName("segment")] string? Segment,
        [property: JsonPropertyName("exchange_token")] JsonElement ExchangeToken,
        [property: JsonPropertyName("instrument_key")] string? Key,
        [property: JsonPropertyName("underlying_symbol")] string? Underlying,
        [property: JsonPropertyName("instrument_type")] string? Side,
        [property: JsonPropertyName("strike_price"), JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] decimal? Strike,
        [property: JsonPropertyName("lot_size"), JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] int? Lot,
        [property: JsonPropertyName("expiry"), JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] long? Expiry);
}
