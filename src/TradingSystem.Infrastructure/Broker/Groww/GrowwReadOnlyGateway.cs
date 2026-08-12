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
    IOptions<GrowwOptions> options) : IGrowwReadOnlyGateway
{
    internal const string ApiClientName = "GrowwReadOnlyApi";
    internal const string InstrumentClientName = "GrowwInstrumentMaster";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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
        var path = "/v1/live-data/quote" + BuildQuery(
            ("exchange", request.Exchange),
            ("segment", request.Segment),
            ("trading_symbol", request.TradingSymbol));
        var payload = await GetPayloadAsync<QuotePayload>(path, cancellationToken);
        if (payload.LastPrice <= 0 || payload.LastTradeTime is <= 0)
        {
            throw Malformed("Groww quote omitted a valid last price or supplied an invalid trade timestamp.");
        }

        return new GrowwQuote(
            payload.LastPrice,
            payload.LastTradeTime,
            payload.BidPrice,
            payload.BidQuantity,
            payload.OfferPrice,
            payload.OfferQuantity,
            payload.Volume,
            payload.OpenInterest,
            payload.OpenInterestDayChange);
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
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        var token = await accessTokenProvider.GetAccessTokenAsync(cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("X-API-VERSION", "1.0");

        using var response = await httpClientFactory.CreateClient(ApiClientName)
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
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
        [property: JsonPropertyName("oi_day_change")] decimal? OpenInterestDayChange);

    private sealed record HistoricalPayload(
        [property: JsonPropertyName("candles")] JsonElement[]? Candles,
        [property: JsonPropertyName("closing_price")] decimal ClosingPrice,
        [property: JsonPropertyName("interval_in_minutes")] int IntervalInMinutes);

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
}
