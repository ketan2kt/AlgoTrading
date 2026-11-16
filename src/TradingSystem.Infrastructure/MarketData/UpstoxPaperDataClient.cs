using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TradingSystem.Application.Broker;
using TradingSystem.Application.MarketData;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.Infrastructure.MarketData;

// Read-only API surface. No order, account, token-renewal or arming operations.
internal sealed class UpstoxPaperDataClient(IHttpClientFactory clients, IConfiguration configuration,
    IServiceScopeFactory scopes, UpstoxPaperQuoteCache quotes, TimeProvider clock,
    IHistoricalQuoteArchive? archive = null) : IDisposable
{
    private readonly SemaphoreSlim catalogGate = new(1, 1);
    private readonly SemaphoreSlim quoteGate = new(1, 1);
    private readonly ConcurrentDictionary<string, DateTimeOffset> requested = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, GrowwQuoteRequest> identities = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (GrowwQuote Quote, DateTimeOffset Observed, DateTimeOffset Liquidity)> recorded = new(StringComparer.Ordinal);
    private Dictionary<string, JsonElement> catalog = new(StringComparer.Ordinal);
    private DateOnly catalogDate;
    private DateTimeOffset nextQuoteAt;
    private long retryNotBeforeTicks;
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, GrowwHistoricalCandles Data)> histories = new();
    private readonly ConcurrentDictionary<GrowwOptionChainRequest, (DateTimeOffset At, GrowwOptionChain Data)> chains = new();
    public bool Enabled => !string.IsNullOrWhiteSpace(configuration["ReplayBackup:UpstoxAnalyticsToken"]);
    public Task PrepareAsync(CancellationToken token) => EnsureCatalogAsync(token);
    private DateOnly Today => DateOnly.FromDateTime(clock.GetUtcNow().ToOffset(TimeSpan.FromHours(5.5)).DateTime);

    public async Task<GrowwQuote> GetQuoteAsync(GrowwQuoteRequest request, CancellationToken token)
    {
        var instrument = await FindAsync(request.Exchange, request.TradingSymbol, false, token);
        var key = await KeyAsync(instrument, token);
        identities[key] = request;
        if (quotes.TryGet(key, clock.GetUtcNow(), out var cached)) return cached;
        requested[key] = clock.GetUtcNow();
        await quoteGate.WaitAsync(token);
        try
        {
            if (quotes.TryGet(key, clock.GetUtcNow(), out cached)) return cached;
            var delay = nextQuoteAt - clock.GetUtcNow();
            if (delay > TimeSpan.Zero) await Task.Delay(delay, clock, token);
            var now = clock.GetUtcNow();
            foreach (var old in requested.Where(x => now - x.Value > TimeSpan.FromMinutes(1)).Select(x => x.Key))
                requested.TryRemove(old, out _);
            requested[key] = now;
            var batch = requested.Keys.Order(StringComparer.Ordinal).Take(500).ToArray();
            if (!batch.Contains(key, StringComparer.Ordinal)) throw new InvalidDataException("Paper quote batch exceeds verified capacity.");
            nextQuoteAt = now.AddSeconds(3);
            using var result = await GetAsync("https://api.upstox.com/v3/market-quote/quotes?instrument_key=" +
                Uri.EscapeDataString(string.Join(',', batch)), token);
            var received = clock.GetUtcNow();
            var data = Data(result.RootElement);
            foreach (var row in data.EnumerateObject())
            {
                var identity = Text(row.Value, "instrument_token");
                if (identity is null || !batch.Contains(identity, StringComparer.Ordinal)) continue;
                if (identity.Contains("_INDEX|", StringComparison.Ordinal))
                {
                    if (UpstoxReplayBackupCapture.TryReadIndex(row.Value, received, out var quote, out var at))
                        Save(identity, quote, at);
                }
                else if (UpstoxReplayBackupCapture.TryRead(row.Value, received, out var id, out var quote, out var at))
                    Save(id, quote, at);
            }
            if (!quotes.TryGet(key, clock.GetUtcNow(), out cached))
                throw new InvalidDataException("Upstox paper quote lacks fresh price or a positive, uncrossed bid/ask book.");
            return cached;

            void Save(string identity, GrowwQuote quote, DateTimeOffset at)
            {
                quotes.Observe(identity, quote, at);
                if (!identities.TryGetValue(identity, out var mapped)) return;
                var hasPrior = recorded.TryGetValue(identity, out var prior);
                if (hasPrior && at <= prior.Observed) return;
                var liquidity = hasPrior && prior.Quote.BidPrice == quote.BidPrice && prior.Quote.OfferPrice == quote.OfferPrice &&
                    prior.Quote.BidQuantity == quote.BidQuantity && prior.Quote.OfferQuantity == quote.OfferQuantity ? prior.Liquidity : at;
                if (archive is null || archive.TryRecordBackup(new(Guid.NewGuid(), received, mapped, quote)
                    { Source = HistoricalQuoteObservation.UpstoxSnapshot, BrokerObservedAtUtc = at,
                        BackupLiquidityAtUtc = quote.BidPrice is null ? null : liquidity }))
                    recorded[identity] = (quote, at, liquidity);
            }
        }
        finally { quoteGate.Release(); }
    }

    public async Task<GrowwHistoricalCandles> GetHistoryAsync(GrowwHistoricalCandleRequest request, CancellationToken token)
    {
        if (request.CandleInterval != "1minute" || request.EndUtc <= request.StartUtc || request.EndUtc - request.StartUtc > TimeSpan.FromDays(15))
            throw new InvalidDataException("Paper fallback requires a bounded one-minute history request.");
        var instrument = await FindAsync(request.Exchange, request.GrowwSymbol, true, token);
        var key = await KeyAsync(instrument, token);
        var first = DateOnly.FromDateTime(request.StartUtc.ToOffset(TimeSpan.FromHours(5.5)).DateTime);
        var last = DateOnly.FromDateTime(request.EndUtc.ToOffset(TimeSpan.FromHours(5.5)).DateTime);
        var rows = new List<GrowwHistoricalCandle>();
        for (var day = first; day <= last; day = day.AddDays(1))
        {
            if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            var cacheKey = key + ":" + day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (!histories.TryGetValue(cacheKey, out var saved) || day == Today && clock.GetUtcNow() - saved.At >= TimeSpan.FromSeconds(15))
            {
                var date = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                var path = day == Today ? $"intraday/{Uri.EscapeDataString(key)}/minutes/1" :
                    $"{Uri.EscapeDataString(key)}/minutes/1/{date}/{date}";
                using var result = await GetAsync("https://api.upstox.com/v3/historical-candle/" + path, token);
                saved = (clock.GetUtcNow(), UpstoxHistoricalCandleBackup.Parse(result.RootElement, day));
                histories[cacheKey] = saved;
            }
            rows.AddRange(saved.Data.Candles.Where(x =>
            {
                var at = GrowwHistoricalCandleImporter.ParseIndiaTimestamp(x.SourceTimestamp);
                return at >= request.StartUtc && at.AddMinutes(1) <= request.EndUtc && at.AddMinutes(1) <= clock.GetUtcNow();
            }));
        }
        var ordered = rows.OrderBy(x => x.SourceTimestamp, StringComparer.Ordinal).ToArray();
        return new(ordered, ordered.LastOrDefault()?.Close ?? 0m, 1) { Source = "Upstox" };
    }

    public async Task<GrowwOptionChain> GetChainAsync(GrowwOptionChainRequest request, CancellationToken token)
    {
        if (chains.TryGetValue(request, out var saved) && clock.GetUtcNow() - saved.At < TimeSpan.FromSeconds(3)) return saved.Data;
        var key = UpstoxReplayBackupCapture.IndexKey(request.Underlying) ?? throw new InvalidDataException("Unsupported paper index.");
        if (!key.StartsWith(request.Exchange + "_", StringComparison.Ordinal)) throw new InvalidDataException("Index exchange mismatch.");
        await EnsureCatalogAsync(token);
        using var result = await GetAsync("https://api.upstox.com/v2/option/chain?instrument_key=" + Uri.EscapeDataString(key) +
            "&expiry_date=" + request.ExpiryDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), token);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var instruments = await db.Instruments.AsNoTracking().Where(x => x.Exchange == request.Exchange &&
            x.ExpiryDate == request.ExpiryDate && x.IsActive &&
            (x.Type == InstrumentType.CallOption || x.Type == InstrumentType.PutOption)).ToListAsync(token);
        var verified = instruments.Where(x => catalog.TryGetValue(x.Exchange + "_FO|" + x.ExchangeToken, out var row) && Matches(row, x))
            .ToDictionary(x => x.Exchange + "_FO|" + x.ExchangeToken, StringComparer.Ordinal);
        var strikes = new List<GrowwOptionStrike>();
        decimal underlying = 0;
        foreach (var row in Data(result.RootElement).EnumerateArray())
        {
            if (Text(row, "underlying_key") != key || Text(row, "expiry") != request.ExpiryDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                throw new InvalidDataException("Option chain identity mismatch.");
            underlying = Number(row, "underlying_spot_price");
            var strike = Number(row, "strike_price");
            var call = Contract(row, "call_options", InstrumentType.CallOption, strike);
            var put = Contract(row, "put_options", InstrumentType.PutOption, strike);
            if (call is not null || put is not null) strikes.Add(new(strike, call, put));
        }
        if (underlying <= 0 || strikes.Count == 0) throw new InvalidDataException("No verified option chain contracts.");
        var chain = new GrowwOptionChain(underlying, strikes) { Source = "Upstox" };
        chains[request] = (clock.GetUtcNow(), chain);
        return chain;

        GrowwOptionContract? Contract(JsonElement row, string name, InstrumentType type, decimal strike)
        {
            if (!row.TryGetProperty(name, out var leg) || Text(leg, "instrument_key") is not { } id ||
                !verified.TryGetValue(id, out var instrument) || instrument.Type != type || instrument.StrikePrice != strike) return null;
            var market = leg.GetProperty("market_data");
            GrowwOptionGreeks? greeks = leg.TryGetProperty("option_greeks", out var g) ? new(Number(g, "delta"), Number(g, "gamma"),
                Number(g, "theta"), Number(g, "vega"), Number(g, "rho"), Number(g, "iv")) : null;
            return new(instrument.TradingSymbol, Number(market, "ltp"), Number(market, "oi"), Number(market, "volume"), greeks);
        }
    }

    private async Task<Instrument> FindAsync(string exchange, string symbol, bool historical, CancellationToken token)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        return await db.Instruments.AsNoTracking().SingleOrDefaultAsync(x => x.Exchange == exchange && x.IsActive &&
            (historical ? x.GrowwSymbol == symbol : x.TradingSymbol == symbol), token) ??
            throw new InvalidDataException("Paper instrument is not synchronized.");
    }

    private async Task<string> KeyAsync(Instrument instrument, CancellationToken token)
    {
        await EnsureCatalogAsync(token);
        var key = instrument.Type == InstrumentType.Index ? UpstoxReplayBackupCapture.IndexKey(instrument.TradingSymbol) :
            instrument.Exchange + "_FO|" + instrument.ExchangeToken;
        if (key is null || !catalog.TryGetValue(key, out var row) || !Matches(row, instrument))
            throw new InvalidDataException("Exact Upstox paper contract mapping is unavailable.");
        return key;
    }

    internal static bool Matches(JsonElement row, Instrument instrument)
    {
        if (instrument.Type == InstrumentType.Index) return UpstoxHistoricalCandleBackup.Matches(row, instrument, instrument.TradingSymbol);
        var market = Text(row, "underlying_symbol");
        if (market is not ("NIFTY" or "SENSEX" or "BANKNIFTY") || !instrument.TradingSymbol.StartsWith(market, StringComparison.Ordinal) ||
            Text(row, "instrument_key") != instrument.Exchange + "_FO|" + instrument.ExchangeToken ||
            Text(row, "segment") != instrument.Exchange + "_FO" || Number(row, "lot_size") != instrument.LotSize ||
            instrument.TickSize <= 0m || Number(row, "tick_size") / 100m != InstrumentPriceIncrement.FromGrowwInstrumentMaster(instrument.TickSize) || instrument.ExpiryDate is not { } expiry ||
            !row.TryGetProperty("expiry", out var exp) || !exp.TryGetInt64(out var epoch) || epoch is < 1_000_000_000_000 or > 4_000_000_000_000 ||
            DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(epoch).ToOffset(TimeSpan.FromHours(5.5)).DateTime) != expiry) return false;
        return instrument.Type switch
        {
            InstrumentType.Future => Text(row, "instrument_type") == "FUT",
            InstrumentType.CallOption => Text(row, "instrument_type") == "CE" && Number(row, "strike_price") == instrument.StrikePrice,
            InstrumentType.PutOption => Text(row, "instrument_type") == "PE" && Number(row, "strike_price") == instrument.StrikePrice,
            _ => false
        };
    }

    private async Task EnsureCatalogAsync(CancellationToken token)
    {
        await catalogGate.WaitAsync(token);
        try
        {
            if (catalogDate == Today && catalog.Count > 0) return;
            using var client = clients.CreateClient(UpstoxReplayBackupCapture.CatalogClientName);
            // Buffer compressed bytes before parsing: bounded HTTP lifetime, no slow response stream held during JSON parsing.
            var bytes = await client.GetByteArrayAsync("https://assets.upstox.com/market-quote/instruments/exchange/complete.json.gz", token);
            using var compressed = new MemoryStream(bytes);
            using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
            using var document = await JsonDocument.ParseAsync(gzip, cancellationToken: token);
            var mapped = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var row in document.RootElement.EnumerateArray())
            {
                if (Text(row, "underlying_symbol") is not ("NIFTY" or "SENSEX" or "BANKNIFTY") && Text(row, "instrument_type") != "INDEX") continue;
                if (Text(row, "instrument_key") is { } key && !mapped.TryAdd(key, row.Clone()))
                    throw new InvalidDataException("Duplicate Upstox catalog identity.");
            }
            catalog = mapped;
            catalogDate = Today;
            histories.Clear(); chains.Clear();
            foreach (var key in requested.Where(x => clock.GetUtcNow() - x.Value > TimeSpan.FromMinutes(1)).Select(x => x.Key))
            { requested.TryRemove(key, out _); identities.TryRemove(key, out _); }
        }
        finally { catalogGate.Release(); }
    }

    private async Task<JsonDocument> GetAsync(string uri, CancellationToken token)
    {
        if (clock.GetUtcNow().UtcTicks < Interlocked.Read(ref retryNotBeforeTicks))
            throw new GrowwApiException("Upstox read-only requests are waiting for broker recovery.", "BACKUP_BACKOFF");
        using var client = clients.CreateClient(UpstoxReplayBackupCapture.ClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", configuration["ReplayBackup:UpstoxAnalyticsToken"]);
        using var response = await client.SendAsync(request, token);
        if ((int)response.StatusCode == 429)
        {
            nextQuoteAt = clock.GetUtcNow() + (response.Headers.RetryAfter?.Delta ??
                (response.Headers.RetryAfter?.Date - clock.GetUtcNow()) ?? TimeSpan.FromMinutes(1));
            Interlocked.Exchange(ref retryNotBeforeTicks, nextQuoteAt.UtcTicks);
            throw new GrowwApiException("Upstox read-only quota is backing off.", "BACKUP_RATE_LIMIT", 429);
        }
        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            Interlocked.Exchange(ref retryNotBeforeTicks, clock.GetUtcNow().AddMinutes(1).UtcTicks);
        response.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
    }
    private static JsonElement Data(JsonElement root) => Text(root, "status") == "success" && root.TryGetProperty("data", out var data)
        ? data : throw new InvalidDataException("Upstox data response is invalid.");
    private static string? Text(JsonElement row, string name) => row.ValueKind == JsonValueKind.Object && row.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static decimal Number(JsonElement row, string name) => row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) ? number : 0;
    public void Dispose() { catalogGate.Dispose(); quoteGate.Dispose(); }
}
