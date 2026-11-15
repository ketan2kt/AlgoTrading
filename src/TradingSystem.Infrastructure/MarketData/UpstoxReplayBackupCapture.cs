using System.Globalization;
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

// Recorder only. It uses the exact dated Groww contract plan and never touches live quote or order services.
internal sealed class UpstoxReplayBackupCapture(IHttpClientFactory clients, IConfiguration configuration,
    IHistoricalQuoteArchive archive, TimeProvider clock, ILogger<UpstoxReplayBackupCapture> logger)
{
    internal const string ClientName = "UpstoxReplayBackup";
    internal static readonly TimeSpan MaximumAge = TimeSpan.FromSeconds(30);
    private static readonly Action<ILogger, string, Exception?> Unavailable = LoggerMessage.Define<string>(
        LogLevel.Warning, new EventId(6755, "UpstoxReplayBackupUnavailable"), "Upstox replay backup unavailable ({FailureType}). Primary capture is unaffected.");
    private readonly string? credential = configuration["ReplayBackup:UpstoxAnalyticsToken"];
    public bool Enabled => !string.IsNullOrWhiteSpace(credential);

    public async Task RunAsync(IReadOnlyList<HistoricalFeedInstrument> plan, GrowwHistoricalFeedAccumulator primary,
        DateTimeOffset open, DateTimeOffset close, CancellationToken token)
    {
        if (!Enabled) return;
        var date = DateOnly.FromDateTime(open.ToOffset(TimeSpan.FromHours(5.5)).DateTime);
        if (plan.Count > 500 || plan.Any(item => item.Market is not ("NIFTY" or "SENSEX" or "BANKNIFTY")))
            throw new InvalidDataException("Backup plan exceeds the supported three-market budget.");
        Dictionary<string, HistoricalFeedInstrument>? mapped = null;
        PublishCoverage(new Dictionary<string, HistoricalFeedInstrument>());
        using var client = clients.CreateClient(ClientName);
        while (mapped is null && !token.IsCancellationRequested && clock.GetUtcNow() < close)
        {
            try
            {
                mapped = await LoadPlanAsync(client, plan, token);
                PublishCoverage(mapped);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                foreach (var market in plan.Select(item => item.Market).Distinct()) Report(market, "Instrument catalog: " + error.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(15), clock, token);
            }
        }
        if (mapped is null) return;
        if (clock.GetUtcNow() < open) await Task.Delay(open - clock.GetUtcNow(), clock, token);
        // Warm, independent per-market capture. At most three requests every
        // three seconds (1,800/30 minutes), each <=500 keys. No order API calls.
        // One slow/failed market never serializes the other market's samples.
        await Task.WhenAll(mapped.GroupBy(pair => pair.Value.Market)
            .Select(group => CaptureMarketAsync(group.Key, group.ToDictionary(pair => pair.Key, pair => pair.Value))));

        void PublishCoverage(IReadOnlyDictionary<string, HistoricalFeedInstrument> matches) => archive.SetBackupCoverage(date,
            plan.Where(item => item.Type is InstrumentType.CallOption or InstrumentType.PutOption).GroupBy(item => item.Market)
                .ToDictionary(group => group.Key, group =>
                {
                    var missing = group.Where(item => !matches.ContainsKey(item.Exchange + "_FO|" + item.ExchangeToken))
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
                var delay = TimeSpan.FromSeconds(3);
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
                    var accepted = new HashSet<string>(StringComparer.Ordinal);
                    var refused = false;
                    foreach (var element in data.EnumerateObject())
                    {
                        if (!TryRead(element.Value, received, out var key, out var quote, out var observedAt) || !instruments.TryGetValue(key, out var instrument)) continue;
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
                        if (primary.NeedsBackup(instruments[key], received)) recovering.Add(key);
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
                    Report(market, error is HttpRequestException { StatusCode: { } status } ? "HTTP " + (int)status : error.GetType().Name);
                    if (delay < TimeSpan.FromSeconds(15)) delay = TimeSpan.FromSeconds(15);
                }
                try { await Task.Delay(delay, clock, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            }
        }
    }

    private static async Task<Dictionary<string, HistoricalFeedInstrument>> LoadPlanAsync(HttpClient client,
        IReadOnlyList<HistoricalFeedInstrument> plan, CancellationToken token)
    {
        var byToken = plan.Where(item => item.Type is InstrumentType.CallOption or InstrumentType.PutOption)
            .ToDictionary(instrument => instrument.Exchange + "_FO|" + instrument.ExchangeToken, StringComparer.Ordinal);
        var mapped = new Dictionary<string, HistoricalFeedInstrument>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        using var response = await client.GetAsync("https://assets.upstox.com/market-quote/instruments/exchange/complete.json.gz",
            HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        using var gzip = new GZipStream(await response.Content.ReadAsStreamAsync(token), CompressionMode.Decompress);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        await foreach (var row in JsonSerializer.DeserializeAsyncEnumerable<Instrument>(gzip, cancellationToken: deadline.Token))
        {
            if (row?.Segment is not ("NSE_FO" or "BSE_FO")) continue;
            var exchangeToken = row.ExchangeToken.ValueKind switch
            {
                JsonValueKind.String => row.ExchangeToken.GetString(),
                JsonValueKind.Number => row.ExchangeToken.GetRawText(),
                _ => null
            };
            if (exchangeToken is null) continue;
            var identity = row.Segment + "|" + exchangeToken;
            if (!byToken.TryGetValue(identity, out var instrument)) continue;
            if (!seen.Add(identity)) { mapped.Remove(identity); continue; }
            if (row.Key != identity || row.Strike is not { } strike || row.Lot is not { } lot || row.Expiry is not { } expiry ||
                !Matches(instrument, row.Underlying, row.Side, strike, lot, expiry)) continue;
            mapped.Add(identity, instrument);
        }
        return mapped;
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
