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
        Dictionary<string, HistoricalFeedInstrument>? mapped = null;
        var recorded = new Dictionary<string, (decimal Bid, long BidSize, decimal Ask, long AskSize)>(StringComparer.Ordinal);
        using var client = clients.CreateClient(ClientName);
        while (!token.IsCancellationRequested && clock.GetUtcNow() < close)
        {
            try
            {
                mapped ??= await LoadPlanAsync(client, plan, token);
                if (clock.GetUtcNow() < open) await Task.Delay(open - clock.GetUtcNow() > TimeSpan.Zero ? open - clock.GetUtcNow() : TimeSpan.Zero, clock, token);
                var at = clock.GetUtcNow();
                if (at >= close) return;
                var missing = new List<string>();
                foreach (var pair in mapped)
                    if (primary.NeedsBackup(pair.Value, at)) missing.Add(pair.Key);
                    else recorded.Remove(pair.Key);
                foreach (var batch in missing.Chunk(100))
                {
                    var uri = "https://api.upstox.com/v3/market-quote/quotes?instrument_key=" + Uri.EscapeDataString(string.Join(',', batch));
                    using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
                    using var response = await client.SendAsync(request, token);
                    response.EnsureSuccessStatusCode();
                    using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(token));
                    var received = clock.GetUtcNow();
                    if (received >= close) return;
                    if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                        throw new InvalidDataException("Upstox quote response omitted data.");
                    var requested = batch.ToHashSet(StringComparer.Ordinal);
                    foreach (var element in data.EnumerateObject())
                    {
                        if (!TryRead(element.Value, received, out var key, out var quote, out var observedAt) || !requested.Contains(key)) continue;
                        var instrument = mapped[key];
                        if (!primary.NeedsBackup(instrument, received)) continue;
                        var depth = (quote.BidPrice!.Value, quote.BidQuantity!.Value, quote.OfferPrice!.Value, quote.OfferQuantity!.Value);
                        // A new response timestamp does not mean unchanged displayed liquidity refilled.
                        if (recorded.TryGetValue(key, out var prior) && prior == depth) continue;
                        if (archive.TryRecordBackup(new(Guid.NewGuid(), received,
                                new(instrument.Exchange, instrument.Segment, instrument.TradingSymbol), quote)
                            { Source = HistoricalQuoteObservation.UpstoxSnapshot, BrokerObservedAtUtc = observedAt })) recorded[key] = depth;
                    }
                }
                await Task.Delay(TimeSpan.FromSeconds(2), clock, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                // Do not log broker URLs, exceptions, headers, or response bodies.
                Unavailable(logger, error is HttpRequestException { StatusCode: { } status } ? "HTTP " + (int)status : error.GetType().Name, null);
                await Task.Delay(TimeSpan.FromSeconds(15), clock, token);
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
        if (mapped.Count == 0) throw new InvalidDataException("No eligible Upstox option contracts matched.");
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
