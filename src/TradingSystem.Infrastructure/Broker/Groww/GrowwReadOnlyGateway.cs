using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using TradingSystem.Application.Broker;

namespace TradingSystem.Infrastructure.Broker.Groww;

public sealed class GrowwReadOnlyGateway(
    IHttpClientFactory httpClientFactory,
    IGrowwAccessTokenProvider accessTokenProvider,
    IOptions<GrowwOptions> options,
    TimeProvider? timeProvider = null, IHistoricalQuoteArchive? historicalQuotes = null) : IGrowwReadOnlyGateway, IDisposable
{
    internal const string ApiClientName = "GrowwReadOnlyApi";
    internal const string InstrumentClientName = "GrowwInstrumentMaster";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    // Groww's Live Data tier is capped at 300 requests/minute across quote/LTP/OHLC.
    // Leave headroom for timing jitter while four index workspaces are active.
    private static readonly TimeSpan MinimumApiRequestSpacing = TimeSpan.FromMilliseconds(210);
    private static readonly TimeSpan QuoteCacheLifetime = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan SupplementalRequestSpacing = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan ForegroundQuietPeriod = TimeSpan.FromMilliseconds(500);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim apiRequestGate = new(1, 1);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> quoteGates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CachedQuote> quoteCache = new(StringComparer.Ordinal);
    private DateTimeOffset nextApiRequestAtUtc = DateTimeOffset.MinValue;
    private DateTimeOffset nextSupplementalAtUtc = DateTimeOffset.MinValue;
    private long lastForegroundDemandTicks;

    public async Task<GrowwUserProfile> GetUserProfileAsync(CancellationToken cancellationToken)
    {
        var payload = await GetPayloadAsync<UserPayload>(
            "/v1/user/detail",
            cancellationToken);
        if (string.IsNullOrWhiteSpace(payload.VendorUserId) || string.IsNullOrWhiteSpace(payload.Ucc))
        {
            throw Malformed("Groww user profile omitted required identifiers.");
        }

        return new GrowwUserProfile(
            payload.VendorUserId,
            payload.Ucc,
            payload.NseEnabled,
            payload.BseEnabled,
            payload.DdpiEnabled,
            payload.ActiveSegments ?? []);
    }

    public async Task<GrowwQuote> GetQuoteAsync(
        GrowwQuoteRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateIdentifier(request.Exchange, nameof(request.Exchange));
        ValidateIdentifier(request.Segment, nameof(request.Segment));
        ValidateIdentifier(request.TradingSymbol, nameof(request.TradingSymbol));
        var cacheKey = $"{request.Exchange}|{request.Segment}|{request.TradingSymbol}";
        if (TryGetCachedQuote(cacheKey, out var cached)) return cached;

        var quoteGate = quoteGates.GetOrAdd(cacheKey, static _ => new SemaphoreSlim(1, 1));
        await quoteGate.WaitAsync(cancellationToken);
        try
        {
            if (TryGetCachedQuote(cacheKey, out cached)) return cached;
            var quote = await GetQuoteCoreAsync(request, cancellationToken);
            var receivedAt = clock.GetUtcNow();
            quoteCache[cacheKey] = new CachedQuote(quote, receivedAt);
            historicalQuotes?.TryRecord(new(Guid.NewGuid(), receivedAt, request, quote));
            return quote;
        }
        finally
        {
            quoteGate.Release();
        }
    }

    internal bool HasRecentReplayReceipt(GrowwQuoteRequest request, DateTimeOffset now) =>
        quoteCache.TryGetValue($"{request.Exchange}|{request.Segment}|{request.TradingSymbol}", out var current) &&
        current.ReceivedAtUtc <= now && now - current.ReceivedAtUtc <= TimeSpan.FromSeconds(5);

    internal async Task<bool> TryRecordSupplementalQuoteAsync(GrowwQuoteRequest request, string accessToken, CancellationToken cancellationToken)
    {
        if (historicalQuotes is null || string.IsNullOrWhiteSpace(accessToken) || request.Segment != "FNO" || request.Exchange is not ("NSE" or "BSE") ||
            !(request.TradingSymbol.EndsWith("CE", StringComparison.Ordinal) || request.TradingSymbol.EndsWith("PE", StringComparison.Ordinal))) return false;
        ValidateIdentifier(request.TradingSymbol, nameof(request.TradingSymbol));
        if (HasRecentReplayReceipt(request, clock.GetUtcNow())) return false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(600), clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
        try
        {
            var quote = await GetQuoteCoreAsync(request, linked.Token, supplementalToken: accessToken);
            var now = clock.GetUtcNow();
            var source = GrowwQuoteTimestamp.ToUtc(quote.LastTradeTimeEpochMilliseconds);
            if (source is null || source > now || now - source > TimeSpan.FromSeconds(30) ||
                quote.BidPrice is not > 0m || quote.OfferPrice is not > 0m || quote.BidPrice > quote.OfferPrice ||
                quote.BidQuantity is not > 0 || quote.OfferQuantity is not > 0) return false;
            // A receipt time is not a book event time. Never promote this
            // observation into the streaming evidence or trading quote cache.
            historicalQuotes.TryRecord(new(Guid.NewGuid(), now, request, quote) { Source = HistoricalQuoteObservation.GrowwRestFallback });
            return true;
        }
        catch (SupplementalBudgetUnavailableException) { return false; }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested) { return false; }
    }

    private async Task<GrowwQuote> GetQuoteCoreAsync(
        GrowwQuoteRequest request,
        CancellationToken cancellationToken, string? supplementalToken = null)
    {
        // Groww's full quote endpoint does not consistently accept MCX commodity
        // trading symbols. The documented multi-LTP endpoint does, so commodity
        // polling must not depend on a particular quote error message to reach it.
        if (string.Equals(request.Exchange, "MCX", StringComparison.Ordinal) &&
            string.Equals(request.Segment, "COMMODITY", StringComparison.Ordinal))
            return await GetCommodityLtpFallbackAsync(request, cancellationToken);
        var path = "/v1/live-data/quote" + BuildQuery(
            ("exchange", request.Exchange),
            ("segment", request.Segment),
            ("trading_symbol", request.TradingSymbol));
        var payload = await GetPayloadAsync<QuotePayload>(path, cancellationToken, supplementalToken);
        if (payload.LastPrice <= 0 || payload.LastTradeTime is <= 0)
        {
            throw Malformed("Groww quote omitted a valid last price or supplied an invalid trade timestamp.");
        }

        // Groww may supply the book only in depth.buy/depth.sell. Do not
        // discard that evidence or manufacture a zero spread when root fields
        // are absent. Explicit zero root fields still mean a withdrawn side.
        var bid = payload.Depth?.Buy?.Where(level => level.Price > 0 && level.Quantity > 0)
            .OrderByDescending(level => level.Price).FirstOrDefault();
        var ask = payload.Depth?.Sell?.Where(level => level.Price > 0 && level.Quantity > 0)
            .OrderBy(level => level.Price).FirstOrDefault();
        return new GrowwQuote(
            payload.LastPrice,
            payload.LastTradeTime,
            payload.BidPrice ?? bid?.Price,
            payload.BidQuantity ?? (payload.BidPrice is null ? bid?.Quantity : null),
            payload.OfferPrice ?? ask?.Price,
            payload.OfferQuantity ?? (payload.OfferPrice is null ? ask?.Quantity : null),
            payload.Volume,
            payload.OpenInterest,
            payload.OpenInterestDayChange);
    }

    private bool TryGetCachedQuote(string cacheKey, out GrowwQuote quote)
    {
        if (quoteCache.TryGetValue(cacheKey, out var cached) &&
            clock.GetUtcNow() - cached.ReceivedAtUtc <= QuoteCacheLifetime)
        {
            quote = cached.Quote;
            return true;
        }

        quote = default!;
        return false;
    }

    private async Task<GrowwQuote> GetCommodityLtpFallbackAsync(
        GrowwQuoteRequest request,
        CancellationToken cancellationToken)
    {
        var key = $"{request.Exchange}_{request.TradingSymbol}";
        var payload = await GetPayloadAsync<Dictionary<string, decimal>>(
            "/v1/live-data/ltp" + BuildQuery(
                ("segment", request.Segment),
                ("exchange_symbols", key)),
            cancellationToken);
        if (!payload.TryGetValue(key, out var lastPrice) || lastPrice <= 0)
            throw Malformed("Groww LTP fallback omitted the requested MCX instrument.");
        return new GrowwQuote(lastPrice, null, null, null, null, null, null, null, null);
    }

    public async Task<GrowwHistoricalCandles> GetHistoricalCandlesAsync(
        GrowwHistoricalCandleRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateIdentifier(request.Exchange, nameof(request.Exchange));
        ValidateIdentifier(request.Segment, nameof(request.Segment));
        ValidateIdentifier(request.GrowwSymbol, nameof(request.GrowwSymbol));
        ValidateIdentifier(request.CandleInterval, nameof(request.CandleInterval));
        if (request.StartUtc >= request.EndUtc)
        {
            throw new ArgumentException("Historical start must precede end.", nameof(request));
        }

        var path = "/v1/historical/candles" + BuildQuery(
            ("exchange", request.Exchange),
            ("segment", request.Segment),
            ("groww_symbol", request.GrowwSymbol),
            ("start_time", request.StartUtc.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
            ("end_time", request.EndUtc.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
            ("candle_interval", request.CandleInterval));
        var payload = await GetPayloadAsync<HistoricalPayload>(path, cancellationToken);
        if (payload.Candles is null || payload.IntervalInMinutes <= 0)
        {
            throw Malformed("Groww historical response omitted candles or interval.");
        }

        var candles = payload.Candles
            .Where(value => !IsIncompleteCandle(value))
            .Select(ParseCandle)
            .ToArray();
        if (payload.Candles.Length > 0 && candles.Length == 0)
        {
            throw Malformed("Groww historical response contained no completed candle rows.");
        }

        return new GrowwHistoricalCandles(candles, payload.ClosingPrice, payload.IntervalInMinutes);
    }

    public async Task<IReadOnlyList<DateOnly>> GetHistoricalExpiriesAsync(
        string exchange, string underlyingSymbol, int year, int? month,
        CancellationToken cancellationToken)
    {
        ValidateIdentifier(exchange, nameof(exchange));
        ValidateIdentifier(underlyingSymbol, nameof(underlyingSymbol));
        if (year < 2020 || year > clock.GetUtcNow().Year || month is < 1 or > 12)
            throw new ArgumentOutOfRangeException(nameof(year), "Historical year/month is invalid.");
        var fields = new List<(string, string)>
        {
            ("exchange", exchange), ("underlying_symbol", underlyingSymbol),
            ("year", year.ToString(CultureInfo.InvariantCulture))
        };
        if (month.HasValue)
            fields.Add(("month", month.Value.ToString(CultureInfo.InvariantCulture)));
        var payload = await GetPayloadAsync<HistoricalExpiriesPayload>(
            "/v1/historical/expiries" + BuildQuery([.. fields]), cancellationToken);
        if (payload.Expiries is null)
            throw Malformed("Groww historical expiries response omitted expiries.");
        var expiries = new List<DateOnly>(payload.Expiries.Length);
        foreach (var raw in payload.Expiries)
        {
            if (!DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var expiry))
                throw Malformed("Groww historical expiries response contained an invalid date.");
            expiries.Add(expiry);
        }
        return expiries.Distinct().OrderBy(value => value).ToArray();
    }

    public async Task<IReadOnlyList<string>> GetHistoricalContractsAsync(
        string exchange, string underlyingSymbol, DateOnly expiryDate,
        CancellationToken cancellationToken)
    {
        ValidateIdentifier(exchange, nameof(exchange));
        ValidateIdentifier(underlyingSymbol, nameof(underlyingSymbol));
        var payload = await GetPayloadAsync<HistoricalContractsPayload>(
            "/v1/historical/contracts" + BuildQuery(
                ("exchange", exchange), ("underlying_symbol", underlyingSymbol),
                ("expiry_date", expiryDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))),
            cancellationToken);
        if (payload.Contracts is null || payload.Contracts.Any(string.IsNullOrWhiteSpace))
            throw Malformed("Groww historical contracts response omitted or invalidated contracts.");
        return payload.Contracts.Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal).ToArray();
    }

    public async Task<GrowwOptionChain> GetOptionChainAsync(
        GrowwOptionChainRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateIdentifier(request.Exchange, nameof(request.Exchange));
        ValidateIdentifier(request.Underlying, nameof(request.Underlying));
        var path = $"/v1/option-chain/exchange/{Uri.EscapeDataString(request.Exchange)}" +
                   $"/underlying/{Uri.EscapeDataString(request.Underlying)}" +
                   BuildQuery(("expiry_date", request.ExpiryDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        var payload = await GetPayloadAsync<OptionChainPayload>(path, cancellationToken);
        if (payload.UnderlyingLastPrice <= 0 || payload.Strikes is null || payload.Strikes.Count == 0)
            throw Malformed("Groww option-chain response omitted the underlying price or strikes.");

        var strikes = new List<GrowwOptionStrike>(payload.Strikes.Count);
        foreach (var (key, value) in payload.Strikes)
        {
            if (!decimal.TryParse(key, NumberStyles.Number, CultureInfo.InvariantCulture, out var strike) || strike <= 0)
                throw Malformed($"Groww option-chain returned invalid strike key '{key}'.");
            strikes.Add(new GrowwOptionStrike(strike, MapContract(value.Call), MapContract(value.Put)));
        }

        return new GrowwOptionChain(payload.UnderlyingLastPrice,
            strikes.OrderBy(value => value.StrikePrice).ToArray());
    }

    private static GrowwOptionContract? MapContract(OptionContractPayload? value)
    {
        if (value is null) return null;
        if (string.IsNullOrWhiteSpace(value.TradingSymbol) || value.LastPrice < 0 ||
            value.OpenInterest < 0 || value.Volume < 0)
            throw Malformed("Groww option-chain returned an invalid option contract.");
        var greeks = value.Greeks is null ? null : new GrowwOptionGreeks(
            value.Greeks.Delta, value.Greeks.Gamma, value.Greeks.Theta,
            value.Greeks.Vega, value.Greeks.Rho, value.Greeks.ImpliedVolatility);
        return new GrowwOptionContract(value.TradingSymbol, value.LastPrice,
            value.OpenInterest, value.Volume, greeks);
    }

    public async Task<IReadOnlyList<GrowwInstrumentRecord>> GetInstrumentMasterAsync(
        CancellationToken cancellationToken)
    {
        using var response = await httpClientFactory.CreateClient(InstrumentClientName)
            .GetAsync(options.Value.InstrumentMasterUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        if (response.Content.Headers.ContentLength > options.Value.MaximumInstrumentBytes)
        {
            throw new GrowwApiException("Groww instrument master exceeds the configured size limit.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        var text = await reader.ReadToEndAsync(cancellationToken);
        if (text.Length > options.Value.MaximumInstrumentBytes)
        {
            throw new GrowwApiException("Groww instrument master exceeds the configured size limit.");
        }

        return GrowwInstrumentCsvParser.Parse(text);
    }

    public async Task<IReadOnlyList<GrowwPosition>> GetPositionsAsync(
        string segment, CancellationToken cancellationToken)
    {
        ValidateIdentifier(segment, nameof(segment));
        if (segment is not ("CASH" or "FNO"))
            throw new ArgumentException("Only Groww CASH and FNO positions are supported.", nameof(segment));
        var payload = await GetPayloadAsync<PositionsPayload>(
            "/v1/positions/user" + BuildQuery(("segment", segment)), cancellationToken);
        return (payload.Positions ?? []).Select(value => new GrowwPosition(
            value.TradingSymbol ?? string.Empty, segment, value.Exchange ?? string.Empty,
            value.Product ?? string.Empty, value.Quantity, value.NetPrice,
            value.CreditQuantity, value.CreditPrice, value.DebitQuantity, value.DebitPrice,
            value.NetCarryForwardQuantity, value.NetCarryForwardPrice, value.RealisedPnl)).ToArray();
    }

    private async Task<TPayload> GetPayloadAsync<TPayload>(
        string path,
        CancellationToken cancellationToken, string? supplementalToken = null)
    {
        var supplemental = supplementalToken is not null;
        if (!supplemental) Interlocked.Exchange(ref lastForegroundDemandTicks, clock.GetUtcNow().UtcTicks);
        else if (clock.GetUtcNow().UtcTicks - Interlocked.Read(ref lastForegroundDemandTicks) < ForegroundQuietPeriod.Ticks)
            throw new SupplementalBudgetUnavailableException();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        var token = supplementalToken ?? await accessTokenProvider.GetAccessTokenAsync(cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("X-API-VERSION", "1.0");

        using var response = await SendRateLimitedAsync(request, supplemental, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        try
        {
            var envelope = await response.Content.ReadFromJsonAsync<Envelope<TPayload>>(
                JsonOptions,
                cancellationToken);
            if (envelope is null || !string.Equals(envelope.Status, "SUCCESS", StringComparison.Ordinal) ||
                envelope.Payload is null)
            {
                throw new GrowwApiException(
                    envelope?.Error?.Message ?? "Groww returned a failure response.",
                    envelope?.Error?.Code,
                    (int)response.StatusCode);
            }

            return envelope.Payload;
        }
        catch (JsonException exception)
        {
            throw Malformed("Groww returned malformed JSON.", exception);
        }
    }

    private async Task<HttpResponseMessage> SendRateLimitedAsync(
        HttpRequestMessage request,
        bool supplemental, CancellationToken cancellationToken)
    {
        if (supplemental)
        {
            if (!await apiRequestGate.WaitAsync(0, cancellationToken)) throw new SupplementalBudgetUnavailableException();
        }
        else await apiRequestGate.WaitAsync(cancellationToken);
        try
        {
            var now = clock.GetUtcNow();
            var delay = nextApiRequestAtUtc - now;
            if (supplemental)
            {
                if (now < nextSupplementalAtUtc || delay > TimeSpan.Zero ||
                    now.UtcTicks - Interlocked.Read(ref lastForegroundDemandTicks) < ForegroundQuietPeriod.Ticks)
                    throw new SupplementalBudgetUnavailableException();
                // Consume budget before the HTTP call, including on timeout or 429.
                nextSupplementalAtUtc = now + SupplementalRequestSpacing;
                nextApiRequestAtUtc = now + MinimumApiRequestSpacing;
            }
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, clock, cancellationToken);

            var response = await httpClientFactory.CreateClient(ApiClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            nextApiRequestAtUtc = clock.GetUtcNow() + MinimumApiRequestSpacing;
            return response;
        }
        finally
        {
            apiRequestGate.Release();
        }
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        ErrorEnvelope? error = null;
        try
        {
            error = await response.Content.ReadFromJsonAsync<ErrorEnvelope>(JsonOptions, cancellationToken);
        }
        catch (JsonException)
        {
            // The status code remains authoritative when an error body is malformed.
        }

        throw new GrowwApiException(
            error?.Error?.Message ?? $"Groww HTTP request failed with {(int)response.StatusCode}.",
            error?.Error?.Code,
            (int)response.StatusCode);
    }

    private static GrowwHistoricalCandle ParseCandle(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() is not (6 or 7))
        {
            var observed = value.ValueKind == JsonValueKind.Array
                ? value.GetArrayLength().ToString(System.Globalization.CultureInfo.InvariantCulture)
                : value.ValueKind.ToString();
            throw Malformed($"Groww candle must contain six or seven fields; observed {observed}.");
        }

        var fields = value.EnumerateArray().ToArray();
        if (fields[0].ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(fields[0].GetString()))
        {
            throw Malformed($"Groww candle timestamp has invalid type {fields[0].ValueKind}.");
        }

        return new GrowwHistoricalCandle(
            fields[0].GetString()!,
            ParseRequiredDecimal(fields[1], "open"),
            ParseRequiredDecimal(fields[2], "high"),
            ParseRequiredDecimal(fields[3], "low"),
            ParseRequiredDecimal(fields[4], "close"),
            ParseVolume(fields[5]),
            fields.Length == 6 || fields[6].ValueKind == JsonValueKind.Null
                ? null
                : ParseRequiredDecimal(fields[6], "open interest"));
    }

    private static bool IsIncompleteCandle(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() is not (6 or 7))
        {
            return false;
        }

        return value.EnumerateArray().Take(5).Any(field => field.ValueKind == JsonValueKind.Null);
    }

    private static decimal ParseRequiredDecimal(JsonElement value, string fieldName)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var parsed))
        {
            return parsed;
        }

        throw Malformed($"Groww candle {fieldName} has invalid type {value.ValueKind}.");
    }

    private static long ParseVolume(JsonElement value)
    {
        // Groww can return null volume for a still-forming FNO candle. Preserve the
        // candle shape and let downstream completeness/volume rules fail closed.
        if (value.ValueKind == JsonValueKind.Null) return 0;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var parsed) ||
            parsed != decimal.Truncate(parsed) || parsed < long.MinValue || parsed > long.MaxValue)
        {
            throw Malformed($"Groww candle volume is not a whole 64-bit number ({value.ValueKind}).");
        }

        return decimal.ToInt64(parsed);
    }

    private static string BuildQuery(params (string Name, string Value)[] values) =>
        "?" + string.Join("&", values.Select(value =>
            $"{Uri.EscapeDataString(value.Name)}={Uri.EscapeDataString(value.Value)}"));

    private static void ValidateIdentifier(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 120)
        {
            throw new ArgumentException("Groww identifier must contain 1 to 120 characters.", parameterName);
        }
    }

    private static GrowwApiException Malformed(string message, Exception? inner = null) =>
        new(message, "MALFORMED_RESPONSE", null, inner);

    public void Dispose()
    {
        apiRequestGate.Dispose();
        foreach (var gate in quoteGates.Values) gate.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed record Envelope<TPayload>(
        string? Status,
        TPayload? Payload,
        GrowwError? Error);

    private sealed record ErrorEnvelope(string? Status, GrowwError? Error);
    private sealed record GrowwError(string? Code, string? Message);

    private sealed record UserPayload(
        [property: JsonPropertyName("vendor_user_id")] string? VendorUserId,
        [property: JsonPropertyName("ucc")] string? Ucc,
        [property: JsonPropertyName("nse_enabled")] bool NseEnabled,
        [property: JsonPropertyName("bse_enabled")] bool BseEnabled,
        [property: JsonPropertyName("ddpi_enabled")] bool DdpiEnabled,
        [property: JsonPropertyName("active_segments")] string[]? ActiveSegments);

    private sealed record QuotePayload(
        [property: JsonPropertyName("last_price")] decimal LastPrice,
        [property: JsonPropertyName("last_trade_time")] long? LastTradeTime,
        [property: JsonPropertyName("bid_price")] decimal? BidPrice,
        [property: JsonPropertyName("bid_quantity")] long? BidQuantity,
        [property: JsonPropertyName("offer_price")] decimal? OfferPrice,
        [property: JsonPropertyName("offer_quantity")] long? OfferQuantity,
        [property: JsonPropertyName("volume")] decimal? Volume,
        [property: JsonPropertyName("open_interest")] decimal? OpenInterest,
        [property: JsonPropertyName("oi_day_change")] decimal? OpenInterestDayChange,
        [property: JsonPropertyName("depth")] QuoteDepthPayload? Depth);

    private sealed record QuoteDepthPayload(
        [property: JsonPropertyName("buy")] QuoteDepthLevel[]? Buy,
        [property: JsonPropertyName("sell")] QuoteDepthLevel[]? Sell);
    private sealed record QuoteDepthLevel(
        [property: JsonPropertyName("price")] decimal Price,
        [property: JsonPropertyName("quantity")] long Quantity);

    private sealed record HistoricalPayload(
        [property: JsonPropertyName("candles")] JsonElement[]? Candles,
        [property: JsonPropertyName("closing_price")] decimal ClosingPrice,
        [property: JsonPropertyName("interval_in_minutes")] int IntervalInMinutes);

    private sealed record HistoricalExpiriesPayload(
        [property: JsonPropertyName("expiries")] string[]? Expiries);

    private sealed record HistoricalContractsPayload(
        [property: JsonPropertyName("contracts")] string[]? Contracts);

    private sealed record OptionChainPayload(
        [property: JsonPropertyName("underlying_ltp")] decimal UnderlyingLastPrice,
        [property: JsonPropertyName("strikes")] Dictionary<string, OptionStrikePayload>? Strikes);

    private sealed record OptionStrikePayload(
        [property: JsonPropertyName("CE")] OptionContractPayload? Call,
        [property: JsonPropertyName("PE")] OptionContractPayload? Put);

    private sealed record OptionContractPayload(
        [property: JsonPropertyName("trading_symbol")] string? TradingSymbol,
        [property: JsonPropertyName("ltp")] decimal LastPrice,
        [property: JsonPropertyName("open_interest")] decimal OpenInterest,
        [property: JsonPropertyName("volume")] decimal Volume,
        [property: JsonPropertyName("greeks")] OptionGreeksPayload? Greeks);

    private sealed record OptionGreeksPayload(
        [property: JsonPropertyName("delta")] decimal Delta,
        [property: JsonPropertyName("gamma")] decimal Gamma,
        [property: JsonPropertyName("theta")] decimal Theta,
        [property: JsonPropertyName("vega")] decimal Vega,
        [property: JsonPropertyName("rho")] decimal Rho,
        [property: JsonPropertyName("iv")] decimal ImpliedVolatility);

    private sealed record PositionsPayload(
        [property: JsonPropertyName("positions")] PositionPayload[]? Positions);

    private sealed record PositionPayload(
        [property: JsonPropertyName("trading_symbol")] string? TradingSymbol,
        [property: JsonPropertyName("credit_quantity")] int CreditQuantity,
        [property: JsonPropertyName("credit_price")] decimal CreditPrice,
        [property: JsonPropertyName("debit_quantity")] int DebitQuantity,
        [property: JsonPropertyName("debit_price")] decimal DebitPrice,
        [property: JsonPropertyName("exchange")] string? Exchange,
        [property: JsonPropertyName("quantity")] int Quantity,
        [property: JsonPropertyName("product")] string? Product,
        [property: JsonPropertyName("net_price")] decimal NetPrice,
        [property: JsonPropertyName("net_carry_forward_quantity")] int NetCarryForwardQuantity,
        [property: JsonPropertyName("net_carry_forward_price")] decimal NetCarryForwardPrice,
        [property: JsonPropertyName("realised_pnl")] decimal RealisedPnl);

    private sealed record CachedQuote(GrowwQuote Quote, DateTimeOffset ReceivedAtUtc);
    private sealed class SupplementalBudgetUnavailableException : Exception;
}
