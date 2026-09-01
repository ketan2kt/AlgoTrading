using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TradingSystem.Application.Broker;
using TradingSystem.Domain;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.Infrastructure.Broker.Groww;

/// <summary>
/// Groww F&amp;O execution adapter. Submission is deliberately never retried. If the
/// create response is ambiguous, the client reference is queried before the
/// caller receives an unknown-outcome result.
/// </summary>
public sealed class GrowwBrokerGateway(
    IHttpClientFactory httpClientFactory,
    IGrowwAccessTokenProvider accessTokenProvider,
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider) : IBrokerGateway, ILiveBrokerProtectionGateway
{
    internal const string ApiClientName = "GrowwExecutionApi";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public TradingMode Mode => TradingMode.Live;

    public async Task<BrokerOrderSnapshot> SubmitAsync(BrokerOrderRequest request,
        CancellationToken cancellationToken)
    {
        ValidateLiveRequest(request);
        var existing = await GetOrderAsync(request.ClientReference, cancellationToken);
        if (existing is not null)
            return existing;

        var body = new
        {
            trading_symbol = request.TradingSymbol,
            quantity = request.Quantity,
            price = request.OrderType == "MARKET" ? 0m : request.ExecutionPrice,
            trigger_price = request.TriggerPrice ?? 0m,
            validity = "DAY",
            exchange = request.Exchange,
            segment = request.Segment,
            product = request.Product,
            order_type = request.OrderType,
            transaction_type = request.Direction == Direction.Buy ? "BUY" : "SELL",
            order_reference_id = request.ClientReference
        };

        try
        {
            var payload = await SendAsync<CreatePayload>(HttpMethod.Post, "/v1/order/create", body,
                cancellationToken);
            return new BrokerOrderSnapshot(request.ClientReference, Required(payload.GrowwOrderId),
                request.InstrumentId, request.Direction, request.Quantity, 0, null,
                MapState(payload.OrderStatus, 0, request.Quantity), timeProvider.GetUtcNow());
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            try
            {
                var recovered = await GetOrderAsync(request.ClientReference, cancellationToken);
                if (recovered is not null)
                    return recovered;
            }
            catch (Exception lookupException) when (lookupException is not OperationCanceledException)
            {
                throw new BrokerOrderOutcomeUnknownException(request.ClientReference,
                    new AggregateException(exception, lookupException));
            }

            throw new BrokerOrderOutcomeUnknownException(request.ClientReference, exception);
        }
    }

    public async Task<BrokerOrderSnapshot?> GetOrderAsync(string clientReference,
        CancellationToken cancellationToken)
    {
        ValidateReference(clientReference);
        try
        {
            var payload = await SendAsync<StatusPayload>(HttpMethod.Get,
                $"/v1/order/status/reference/{Uri.EscapeDataString(clientReference)}?segment=FNO", null,
                cancellationToken);
            var local = await ResolveOrderContextAsync(clientReference, cancellationToken);
            var average = await GetAverageFillPriceAsync(payload.GrowwOrderId, cancellationToken);
            return new BrokerOrderSnapshot(clientReference, Required(payload.GrowwOrderId),
                local?.InstrumentId ?? Guid.Empty, local?.Direction ?? Direction.Buy,
                local?.RequestedQuantity ?? payload.FilledQuantity, payload.FilledQuantity,
                average, MapState(payload.OrderStatus, payload.FilledQuantity,
                    local?.RequestedQuantity ?? payload.FilledQuantity),
                timeProvider.GetUtcNow());
        }
        catch (GrowwApiException exception) when (exception.StatusCode == 404)
        {
            return null;
        }
    }

    public async Task<BrokerOrderSnapshot> CancelAsync(string clientReference,
        CancellationToken cancellationToken)
    {
        var order = await GetOrderAsync(clientReference, cancellationToken)
                    ?? throw new BrokerOrderNotFoundException(clientReference);
        var payload = await SendAsync<CreatePayload>(HttpMethod.Post, "/v1/order/cancel",
            new { segment = "FNO", groww_order_id = order.BrokerOrderId }, cancellationToken);
        return order with
        {
            State = MapState(payload.OrderStatus, order.FilledQuantity, order.RequestedQuantity),
            UpdatedAtUtc = timeProvider.GetUtcNow()
        };
    }

    public async Task<BrokerProtectionSnapshot> CreateProtectionAsync(
        BrokerProtectionRequest request, CancellationToken cancellationToken)
    {
        ValidateReference(request.ClientReference);
        if (string.IsNullOrWhiteSpace(request.TradingSymbol) ||
            request.Exchange is not ("NSE" or "BSE") || request.Quantity <= 0 ||
            request.NetPositionQuantity == 0 || request.Quantity > Math.Abs(request.NetPositionQuantity) ||
            request.Target <= 0 || request.StopLoss <= 0)
            throw new ArgumentException("Groww OCO protection fields are invalid.", nameof(request));
        var transaction = request.NetPositionQuantity > 0 ? "SELL" : "BUY";
        var payload = await SendAsync<SmartOrderPayload>(HttpMethod.Post, "/v1/order-advance/create",
            new
            {
                reference_id = request.ClientReference,
                smart_order_type = "OCO",
                segment = "FNO",
                trading_symbol = request.TradingSymbol,
                quantity = request.Quantity,
                net_position_quantity = request.NetPositionQuantity,
                transaction_type = transaction,
                target = new { trigger_price = FormatPrice(request.Target), order_type = "LIMIT", price = FormatPrice(request.Target) },
                stop_loss = new { trigger_price = FormatPrice(request.StopLoss), order_type = "SL_M", price = (string?)null },
                product_type = "NRML",
                exchange = request.Exchange,
                duration = "DAY"
            }, cancellationToken);
        return MapProtection(request.ClientReference, payload, request.Quantity, request.Target,
            request.StopLoss);
    }

    public async Task<BrokerProtectionSnapshot> ModifyProtectionAsync(string brokerProtectionId,
        int quantity, decimal target, decimal stopLoss, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(brokerProtectionId) || quantity <= 0 || target <= 0 || stopLoss <= 0)
            throw new ArgumentException("Groww OCO modification fields are invalid.");
        var payload = await SendAsync<SmartOrderPayload>(HttpMethod.Put,
            $"/v1/order-advance/modify/{Uri.EscapeDataString(brokerProtectionId)}", new
            {
                smart_order_type = "OCO",
                segment = "FNO",
                duration = "DAY",
                quantity,
                product_type = "NRML",
                target = new { trigger_price = FormatPrice(target) },
                stop_loss = new { trigger_price = FormatPrice(stopLoss) }
            }, cancellationToken);
        return MapProtection(string.Empty, payload, quantity, target, stopLoss);
    }

    public async Task CancelProtectionAsync(string brokerProtectionId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(brokerProtectionId))
            throw new ArgumentException("Groww OCO identifier is required.", nameof(brokerProtectionId));
        _ = await SendAsync<SmartOrderPayload>(HttpMethod.Post,
            $"/v1/order-advance/cancel/FNO/OCO/{Uri.EscapeDataString(brokerProtectionId)}", null,
            cancellationToken);
    }

    public async Task<IReadOnlyList<BrokerPositionSnapshot>> GetPositionsAsync(
        CancellationToken cancellationToken)
    {
        var payload = await SendAsync<PositionsPayload>(HttpMethod.Get,
            "/v1/positions/user?segment=FNO", null, cancellationToken);
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var instruments = await db.Instruments.AsNoTracking()
            .Where(value => value.IsActive)
            .ToDictionaryAsync(value => value.TradingSymbol, StringComparer.Ordinal, cancellationToken);
        var result = new List<BrokerPositionSnapshot>();
        foreach (var position in payload.Positions ?? [])
        {
            if (position.Quantity == 0 || string.IsNullOrWhiteSpace(position.TradingSymbol) ||
                !instruments.TryGetValue(position.TradingSymbol, out var instrument))
                continue;
            result.Add(new BrokerPositionSnapshot(instrument.Id,
                position.Quantity > 0 ? Direction.Buy : Direction.Sell,
                Math.Abs(position.Quantity), position.NetPrice));
        }
        return result;
    }

    public async Task<BrokerReconciliationResult> ReconcileAsync(
        IReadOnlyCollection<ExpectedBrokerPosition> expectedPositions,
        CancellationToken cancellationToken)
    {
        var actual = await GetPositionsAsync(cancellationToken);
        var expectedMap = expectedPositions.GroupBy(x => (x.InstrumentId, x.Direction))
            .ToDictionary(x => x.Key, x => x.Sum(v => v.Quantity));
        var actualMap = actual.GroupBy(x => (x.InstrumentId, x.Direction))
            .ToDictionary(x => x.Key, x => x.Sum(v => v.Quantity));
        var mismatches = new List<string>();
        foreach (var key in expectedMap.Keys.Union(actualMap.Keys))
        {
            expectedMap.TryGetValue(key, out var expected);
            actualMap.TryGetValue(key, out var observed);
            if (expected != observed)
                mismatches.Add($"{key.InstrumentId:N}/{key.Direction}: expected {expected}, actual {observed}.");
        }
        return new BrokerReconciliationResult(mismatches.Count == 0, mismatches.Count == 0,
            mismatches, timeProvider.GetUtcNow());
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
            await accessTokenProvider.GetAccessTokenAsync(cancellationToken));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("X-API-VERSION", "1.0");
        if (body is not null)
            request.Content = JsonContent.Create(body, options: JsonOptions);
        using var response = await httpClientFactory.CreateClient(ApiClientName)
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new GrowwApiException($"Groww execution API returned {(int)response.StatusCode}.",
                statusCode: (int)response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<Envelope<T>>(JsonOptions, cancellationToken)
                       ?? throw new GrowwApiException("Groww execution API returned an empty response.");
        if (!string.Equals(envelope.Status, "SUCCESS", StringComparison.OrdinalIgnoreCase) || envelope.Payload is null)
            throw new GrowwApiException("Groww execution API reported failure.");
        return envelope.Payload;
    }

    private async Task<decimal?> GetAverageFillPriceAsync(string? orderId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(orderId)) return null;
        var payload = await SendAsync<TradesPayload>(HttpMethod.Get,
            $"/v1/order/trades/{Uri.EscapeDataString(orderId)}?segment=FNO&page=0&page_size=50", null,
            cancellationToken);
        var trades = payload.TradeList ?? [];
        var quantity = trades.Sum(x => x.Quantity);
        return quantity == 0 ? null : trades.Sum(x => x.Price * x.Quantity) / quantity;
    }

    private async Task<LocalOrderContext?> ResolveOrderContextAsync(string clientReference,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        return await (from order in db.Orders.AsNoTracking()
                join risk in db.RiskDecisions.AsNoTracking() on order.RiskDecisionId equals risk.Id
                join signal in db.Signals.AsNoTracking() on risk.SignalId equals signal.Id
                where order.Mode == TradingMode.Live && order.ClientReference == clientReference
                select new LocalOrderContext(signal.InstrumentId, signal.Direction, order.RequestedQuantity))
            .SingleOrDefaultAsync(cancellationToken);
    }

    private static void ValidateLiveRequest(BrokerOrderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateReference(request.ClientReference);
        if (request.InstrumentId == Guid.Empty || request.Quantity <= 0 ||
            string.IsNullOrWhiteSpace(request.TradingSymbol) ||
            request.Exchange is not ("NSE" or "BSE") || request.Segment != "FNO" ||
            request.Product is not ("MIS" or "NRML") ||
            request.OrderType is not ("MARKET" or "LIMIT" or "SL" or "SL_M"))
            throw new ArgumentException("Live Groww F&O order fields are invalid.", nameof(request));
    }

    private static void ValidateReference(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length is < 8 or > 20 ||
            value.Count(c => c == '-') > 2 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            throw new ArgumentException("Groww order reference must be 8-20 alphanumeric characters with at most two hyphens.");
    }

    private static string Required(string? value) => !string.IsNullOrWhiteSpace(value)
        ? value : throw new GrowwApiException("Groww response omitted the order id.");

    private static OrderState MapState(string? status, int filled, int requested) => status?.ToUpperInvariant() switch
    {
        "COMPLETED" or "EXECUTED" when requested > 0 && filled >= requested => OrderState.Filled,
        "COMPLETED" or "EXECUTED" => OrderState.PartiallyFilled,
        "OPEN" or "PENDING" or "NEW" or "PLACED" => filled > 0 ? OrderState.PartiallyFilled : OrderState.BrokerAcknowledged,
        "CANCELLED" => OrderState.Cancelled,
        "REJECTED" or "FAILED" => OrderState.RejectedByBroker,
        _ => OrderState.ReconciliationRequired
    };

    private BrokerProtectionSnapshot MapProtection(string reference, SmartOrderPayload payload,
        int quantity, decimal target, decimal stopLoss) => new(reference,
        Required(payload.SmartOrderId), payload.Status ?? "UNKNOWN", quantity, target, stopLoss,
        timeProvider.GetUtcNow());

    private static string FormatPrice(decimal value) =>
        value.ToString("0.00##", System.Globalization.CultureInfo.InvariantCulture);

    private sealed record Envelope<T>([property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("payload")] T? Payload);
    private sealed record CreatePayload([property: JsonPropertyName("groww_order_id")] string? GrowwOrderId,
        [property: JsonPropertyName("order_status")] string? OrderStatus);
    private sealed record StatusPayload([property: JsonPropertyName("groww_order_id")] string? GrowwOrderId,
        [property: JsonPropertyName("order_status")] string? OrderStatus,
        [property: JsonPropertyName("filled_quantity")] int FilledQuantity);
    private sealed record TradesPayload([property: JsonPropertyName("trade_list")] TradePayload[]? TradeList);
    private sealed record TradePayload([property: JsonPropertyName("price")] decimal Price,
        [property: JsonPropertyName("quantity")] int Quantity);
    private sealed record PositionsPayload([property: JsonPropertyName("positions")] PositionPayload[]? Positions);
    private sealed record PositionPayload([property: JsonPropertyName("trading_symbol")] string? TradingSymbol,
        [property: JsonPropertyName("quantity")] int Quantity,
        [property: JsonPropertyName("net_price")] decimal NetPrice);
    private sealed record LocalOrderContext(Guid InstrumentId, Direction Direction, int RequestedQuantity);
    private sealed record SmartOrderPayload(
        [property: JsonPropertyName("smart_order_id")] string? SmartOrderId,
        [property: JsonPropertyName("status")] string? Status);
}
