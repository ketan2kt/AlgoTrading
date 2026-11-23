using System.Globalization;
using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TradingSystem.Application.Broker;
using TradingSystem.Domain;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Execution;
using TradingSystem.Infrastructure.MarketData;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.Infrastructure.Broker.Upstox;

/// <summary>Account-pinned execution only. Never reads the analytics token or retries an uncertain write.</summary>
internal sealed class UpstoxBrokerGateway(UpstoxExecutionHttp http, UpstoxPaperDataClient contracts,
    IServiceScopeFactory scopes, IOptions<UpstoxExecutionOptions> options, TimeProvider clock) : ILiveExecutionBrokerGateway
{
    private readonly ConcurrentDictionary<string, (Direction Side, int Quantity, decimal Price, DateTimeOffset At)> pricedFills = new(StringComparer.Ordinal);
    public string Broker => "Upstox";
    public string Account => options.Value.AccountId;
    public TradingMode Mode => TradingMode.Live;
    public decimal ProtectionLimit(decimal stop) => TradingSystem.Infrastructure.Broker.Groww.GrowwBrokerGateway.ProtectiveLimit(stop);

    public async Task<BrokerOrderSnapshot> SubmitAsync(BrokerOrderRequest request, CancellationToken token)
    {
        Validate(request);
        var instrument = await InstrumentAsync(request.InstrumentId, token);
        var contract = await contracts.ExecutionContractAsync(instrument, token);
        if (request.TradingSymbol != instrument.TradingSymbol || request.Exchange != instrument.Exchange ||
            request.Quantity % instrument.LotSize != 0 || request.Quantity > contract.FreezeQuantity)
            throw new InvalidOperationException("Upstox contract/lot/freeze validation failed. No sliced or guessed order submitted.");
        await RequireOwnedReferenceAsync(request.ClientReference, instrument.Id, token);
        var existing = await GetOrderAsync(request.ClientReference, token);
        if (existing is not null) return existing;
        var payload = new {
            quantity = request.Quantity, product = "D", validity = "DAY", price = request.ExecutionPrice,
            tag = request.ClientReference, instrument_token = contract.Key, order_type = request.OrderType,
            transaction_type = request.Direction == Direction.Buy ? "BUY" : "SELL", disclosed_quantity = 0,
            trigger_price = request.TriggerPrice ?? 0, is_amo = false, slice = false, market_protection = -1
        };
        try
        {
            var accepted = await http.SendAsync(HttpMethod.Post, "v3/order/place", payload, token);
            var ids = accepted.GetProperty("order_ids").EnumerateArray().Select(x => x.GetString()).ToArray();
            if (ids.Length != 1 || string.IsNullOrWhiteSpace(ids[0])) throw new InvalidDataException("Unexpected Upstox order identity.");
            return new(request.ClientReference, ids[0]!, instrument.Id, request.Direction, request.Quantity,
                0, null, OrderState.BrokerAcknowledged, clock.GetUtcNow());
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidDataException)
        {
            // The durable intent already says submitting. Even an empty lookup is NOT permission to resend.
            throw new BrokerOrderOutcomeUnknownException(request.ClientReference, ex);
        }
    }

    public async Task<BrokerOrderSnapshot?> GetOrderAsync(string reference, CancellationToken token)
    {
        ValidateReference(reference);
        var rows = Array(await http.SendAsync(HttpMethod.Get, "v2/order/retrieve-all", null, token));
        var matches = rows.Where(x => OptionalText(x, "tag") == reference).ToArray();
        if (matches.Length == 0) return null;
        if (matches.Length != 1) throw new BrokerOrderOutcomeUnknownException(reference);
        var row = matches[0];
        var instrument = await InstrumentKeyAsync(Text(row, "instrument_token"), token);
        await RequireOwnedReferenceAsync(reference, instrument.Id, token);
        RequireOrderIdentity(row, reference, instrument);
        var qty = Integer(row, "quantity"); var filled = Integer(row, "filled_quantity");
        var fills = await FillsAsync(Text(row, "order_id"), Side(row), filled, token);
        if (fills.Quantity > filled) throw new InvalidDataException("Upstox fills exceed order status; retry read reconciliation.");
        return new(reference, Text(row, "order_id"), instrument.Id, Side(row), qty, filled,
            fills.Quantity == filled && filled > 0 ? fills.Average : null,
            MapState(Text(row, "status"), filled, qty), clock.GetUtcNow()) { PricedQuantity = fills.Quantity };
    }

    public async Task<BrokerOrderSnapshot> CancelAsync(string reference, CancellationToken token)
    {
        var order = await GetOrderAsync(reference, token) ?? throw new BrokerOrderNotFoundException(reference);
        if (order.State is OrderState.Cancelled or OrderState.Filled or OrderState.RejectedByBroker) return order;
        await CancelByIdAsync(order.BrokerOrderId, token);
        return await GetOrderAsync(reference, token) ?? throw new BrokerOrderOutcomeUnknownException(reference);
    }

    public async Task<BrokerProtectionSnapshot> CreateProtectionAsync(BrokerProtectionRequest request, CancellationToken token)
    {
        var placed = await SubmitAsync(new(request.ClientReference, request.InstrumentId, Direction.Sell,
            request.Quantity, ProtectionLimit(request.StopLoss), TradingSymbol: request.TradingSymbol,
            Exchange: request.Exchange, Product: "NRML", OrderType: "SL", TriggerPrice: request.StopLoss), token);
        return (await ConfirmProtectionAsync(placed.BrokerOrderId, request.Quantity, request.StopLoss, token))
            with { ClientReference = request.ClientReference };
    }

    public async Task<BrokerProtectionSnapshot> ModifyProtectionAsync(string id, int quantity, decimal stopLoss, CancellationToken token)
    {
        var row = await OwnedStopAsync(id, token);
        var instrument = await InstrumentKeyAsync(Text(row, "instrument_token"), token);
        var contract = await contracts.ExecutionContractAsync(instrument, token);
        if (quantity <= 0 || quantity % instrument.LotSize != 0 || quantity > contract.FreezeQuantity ||
            stopLoss <= .05m || stopLoss % .05m != 0 || Integer(row, "filled_quantity") != 0)
            throw new InvalidOperationException("Invalid Upstox protection amendment.");
        await http.SendAsync(HttpMethod.Put, "v3/order/modify", new { order_id = id, quantity,
            validity = "DAY", price = ProtectionLimit(stopLoss), order_type = "SL", disclosed_quantity = 0,
            trigger_price = stopLoss, market_protection = -1 }, token);
        return await ConfirmProtectionAsync(id, quantity, stopLoss, token);
    }

    public async Task<BrokerProtectionSnapshot> ConfirmProtectionAsync(string id, int quantity, decimal stop, CancellationToken token)
    {
        for (var i = 0; i < 5; i++)
        {
            var row = await OwnedStopAsync(id, token);
            if (Text(row, "status") == "trigger pending" && Integer(row, "quantity") == quantity &&
                Integer(row, "filled_quantity") == 0 && Decimal(row, "trigger_price") == stop &&
                Decimal(row, "price") == ProtectionLimit(stop))
                return new(Text(row, "tag"), id, "TRIGGER_PENDING", quantity, stop, clock.GetUtcNow());
            if (Integer(row, "filled_quantity") > 0 || Text(row, "status") is "cancelled" or "rejected" or "complete") break;
            if (i < 4) await Task.Delay(TimeSpan.FromMilliseconds(500), token);
        }
        throw new BrokerOrderOutcomeUnknownException(id);
    }

    public async Task CancelProtectionAsync(string id, CancellationToken token)
    {
        var row = await OwnedStopAsync(id, token);
        if (Text(row, "status") == "cancelled") return;
        if (Integer(row, "filled_quantity") > 0) throw new BrokerOrderOutcomeUnknownException(id);
        await CancelByIdAsync(id, token);
        var confirmed = await OwnedStopAsync(id, token);
        if (Text(confirmed, "status") != "cancelled" || Integer(confirmed, "filled_quantity") != 0)
            throw new BrokerOrderOutcomeUnknownException(id);
    }

    private async Task CancelByIdAsync(string id, CancellationToken token) =>
        _ = await http.SendAsync(HttpMethod.Delete, "v3/order/cancel?order_id=" + Uri.EscapeDataString(id), null, token);

    private async Task<JsonElement> OwnedStopAsync(string id, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Order ID required.");
        var row = await http.SendAsync(HttpMethod.Get, "v2/order/details?order_id=" + Uri.EscapeDataString(id), null, token);
        if (Text(row, "order_id") != id || Side(row) != Direction.Sell || Text(row, "order_type") != "SL" ||
            Text(row, "product") != "D") throw new InvalidDataException("Upstox stop identity does not match.");
        var instrument = await InstrumentKeyAsync(Text(row, "instrument_token"), token);
        var reference = Text(row, "tag");
        if (!reference.StartsWith("LP", StringComparison.Ordinal) && !reference.StartsWith("LR", StringComparison.Ordinal))
            throw new InvalidDataException("Not a managed protective order.");
        await RequireOwnedReferenceAsync(reference, instrument.Id, token);
        RequireOrderIdentity(row, reference, instrument);
        return row;
    }

    public async Task<IReadOnlyList<BrokerPositionSnapshot>> GetPositionsAsync(CancellationToken token)
    {
        var rows = Array(await http.SendAsync(HttpMethod.Get, "v2/portfolio/short-term-positions", null, token));
        var result = new List<BrokerPositionSnapshot>();
        foreach (var row in rows)
        {
            var qty = Integer(row, "quantity");
            if (qty == 0 || Text(row, "product") != "D") continue;
            var key = Text(row, "instrument_token");
            if (!key.StartsWith("NSE_FO|", StringComparison.Ordinal) && !key.StartsWith("BSE_FO|", StringComparison.Ordinal)) continue;
            var instrument = await FindInstrumentKeyAsync(key, token);
            if (instrument is null) continue; // Unrelated account instruments are outside managed exposure.
            result.Add(new(instrument.Id, qty > 0 ? Direction.Buy : Direction.Sell, Math.Abs(qty), Decimal(row, "average_price")));
        }
        return result;
    }

    public async Task<BrokerReconciliationResult> ReconcileAsync(IReadOnlyCollection<ExpectedBrokerPosition> expected,
        CancellationToken token) => AutomaticLiveExecutionService.ReconcileManagedPositions(expected,
            (await GetPositionsAsync(token)).ToArray(), clock.GetUtcNow());

    public async Task<LiveAccountSnapshot> ReadAccountAsync(CancellationToken token)
    {
        var rows = Array(await http.SendAsync(HttpMethod.Get, "v2/order/retrieve-all", null, token));
        var orders = new List<LiveAccountOrder>();
        foreach (var row in rows)
        {
            var key = Text(row, "instrument_token");
            if (!key.StartsWith("NSE_FO|", StringComparison.Ordinal) && !key.StartsWith("BSE_FO|", StringComparison.Ordinal)) continue;
            var instrument = await FindInstrumentKeyAsync(key, token);
            if (instrument is null) continue;
            var qty = Integer(row, "quantity"); var filled = Integer(row, "filled_quantity");
            orders.Add(new(Text(row, "order_id"), OptionalText(row, "tag") ?? "", instrument.TradingSymbol,
                instrument.Exchange, Product(Text(row, "product")), Side(row), qty, filled,
                MapState(Text(row, "status"), filled, qty), IndiaTimestamp(Text(row, "order_timestamp")), Decimal(row, "trigger_price")));
        }
        if (orders.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != orders.Count)
            throw new InvalidDataException("Duplicate Upstox order identities.");
        var positions = new List<LiveAccountPosition>();
        foreach (var row in Array(await http.SendAsync(HttpMethod.Get, "v2/portfolio/short-term-positions", null, token)))
        {
            var key = Text(row, "instrument_token");
            if (!key.StartsWith("NSE_FO|", StringComparison.Ordinal) && !key.StartsWith("BSE_FO|", StringComparison.Ordinal)) continue;
            var instrument = await FindInstrumentKeyAsync(key, token);
            if (instrument is null) continue;
            positions.Add(new(instrument.TradingSymbol, instrument.Exchange, Product(Text(row, "product")), Integer(row, "quantity")));
        }
        return new(orders, positions, clock.GetUtcNow());
    }

    public async Task<ExternalExitFill> ReadExternalFillAsync(string id, int expectedQuantity, CancellationToken token)
    {
        var fills = await FillsAsync(id, Direction.Sell, expectedQuantity, token);
        if (fills.Quantity != expectedQuantity || fills.Quantity <= 0 || fills.Average is null)
            throw new InvalidDataException("Upstox external fills do not cover confirmed units.");
        return new(id, fills.Quantity, fills.Average.Value, clock.GetUtcNow());
    }

    private async Task<(int Quantity, decimal? Average)> FillsAsync(string id, Direction side, int expectedQuantity, CancellationToken token)
    {
        // Immutable priced fills can be reused while the broker's cumulative filled
        // quantity is identical. Missing prices or additional fills always reread.
        if (pricedFills.TryGetValue(id, out var saved) && saved.Side == side && saved.Quantity == expectedQuantity &&
            clock.GetUtcNow() - saved.At < TimeSpan.FromMinutes(2)) return (saved.Quantity, saved.Price);
        var rows = Array(await http.SendAsync(HttpMethod.Get, "v2/order/trades?order_id=" + Uri.EscapeDataString(id), null, token));
        var fills = new Dictionary<string, (int Quantity, decimal Price)>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (Text(row, "order_id") != id || Side(row) != side) throw new InvalidDataException("Upstox fill identity mismatch.");
            var value = (Integer(row, "quantity"), Decimal(row, "average_price"));
            if (value.Item1 <= 0 || value.Item2 <= 0) throw new InvalidDataException("Invalid Upstox fill.");
            var trade = Text(row, "trade_id");
            if (fills.TryGetValue(trade, out var prior) && prior != value) throw new InvalidDataException("Conflicting Upstox fill identity.");
            fills[trade] = value;
        }
        var quantity = fills.Values.Sum(x => x.Quantity);
        decimal? average = quantity == 0 ? null : fills.Values.Sum(x => x.Quantity * x.Price) / quantity;
        if (quantity > 0 && quantity == expectedQuantity && average.HasValue)
        {
            if (pricedFills.Count >= 256) pricedFills.Clear();
            pricedFills[id] = (side, quantity, average.Value, clock.GetUtcNow());
        }
        return (quantity, average);
    }

    private async Task<Instrument> InstrumentAsync(Guid id, CancellationToken token)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<TradingDbContext>().Instruments.AsNoTracking()
            .SingleAsync(x => x.Id == id, token);
    }

    private async Task<Instrument> InstrumentKeyAsync(string key, CancellationToken token)
        => await FindInstrumentKeyAsync(key, token) ?? throw new InvalidDataException("Managed Upstox contract is missing from the current instrument catalog.");

    private async Task<Instrument?> FindInstrumentKeyAsync(string key, CancellationToken token)
    {
        var parts = key.Split('|');
        if (parts.Length != 2 || parts[0] is not ("NSE_FO" or "BSE_FO")) throw new InvalidDataException("Unsupported execution instrument key.");
        var exchange = parts[0][..3]; var exchangeToken = parts[1];
        var today = DateOnly.FromDateTime(clock.GetUtcNow().ToOffset(TimeSpan.FromMinutes(330)).Date);
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<TradingDbContext>().Instruments.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Exchange == exchange && x.ExchangeToken == exchangeToken &&
                x.IsActive && x.ExpiryDate >= today, token);
    }

    private async Task RequireOwnedReferenceAsync(string reference, Guid instrument, CancellationToken token)
    {
        await using var scope = scopes.CreateAsyncScope();
        var intents = await scope.ServiceProvider.GetRequiredService<TradingDbContext>().LiveExecutionIntents.AsNoTracking()
            .Where(x => x.ExecutionBroker == Broker && x.ExecutionAccount == Account && x.InstrumentId == instrument)
            .ToListAsync(token);
        static string Ref(string p, Guid id) => p + id.ToString("N")[..16];
        var owners = intents.Where(x => x.ClientReference == reference || Ref("LP", x.Id) == reference || Ref("LR", x.Id) == reference ||
            Ref("LX", x.SourceId) == reference || Ref("LA", x.SourceId) == reference || Ref("LQ", x.SourceId) == reference).ToArray();
        if (owners.Length != 1) throw new InvalidOperationException("Upstox action is not owned by exactly one persisted execution intent.");
    }

    private void RequireOrderIdentity(JsonElement row, string reference, Instrument instrument)
    {
        if (Text(row, "tag") != reference || Text(row, "placed_by") != Account || Text(row, "product") != "D" ||
            Text(row, "instrument_token") != instrument.Exchange + "_FO|" + instrument.ExchangeToken)
            throw new InvalidDataException("Upstox order identity/account does not match.");
    }

    internal static OrderState MapState(string state, int filled, int requested)
    {
        if (requested <= 0 || filled < 0 || filled > requested) throw new InvalidDataException("Invalid Upstox order quantities.");
        return state switch {
            "complete" when filled == requested => OrderState.Filled,
            "cancelled" => OrderState.Cancelled,
            "rejected" when filled == 0 => OrderState.RejectedByBroker,
            "open" or "trigger pending" => filled > 0 ? OrderState.PartiallyFilled : OrderState.BrokerAcknowledged,
            "cancel pending" => OrderState.CancelPending,
            _ => OrderState.ReconciliationRequired
        };
    }

    internal static DateTimeOffset IndiaTimestamp(string value)
    {
        if (!DateTime.TryParseExact(value, ["yyyy-MM-dd HH:mm:ss", "dd-MMM-yyyy HH:mm:ss"], CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date)) throw new InvalidDataException("Missing Upstox order timestamp.");
        return new(DateTime.SpecifyKind(date, DateTimeKind.Unspecified), TimeSpan.FromMinutes(330));
    }
    private static string Product(string product) => product switch { "D" => "NRML", "I" => "MIS", _ => product };
    private static Direction Side(JsonElement row) => Text(row, "transaction_type") switch {
        "BUY" => Direction.Buy, "SELL" => Direction.Sell, _ => throw new InvalidDataException("Unknown Upstox side.") };
    private static JsonElement[] Array(JsonElement value) => value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToArray() :
        throw new InvalidDataException("Upstox account response omitted its array; not an empty account.");
    private static string? OptionalText(JsonElement row, string key) => row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string Text(JsonElement row, string key) => OptionalText(row, key) is { Length: > 0 } value ? value : throw new InvalidDataException("Missing Upstox field: " + key);
    private static int Integer(JsonElement row, string key) => row.GetProperty(key).GetInt32();
    private static decimal Decimal(JsonElement row, string key) => row.GetProperty(key).GetDecimal();
    private static void ValidateReference(string reference)
    {
        if (reference.Length is < 8 or > 20 || reference.Any(x => !char.IsAsciiLetterOrDigit(x)))
            throw new ArgumentException("Invalid managed execution reference.");
    }
    private static void Validate(BrokerOrderRequest r)
    {
        ValidateReference(r.ClientReference);
        if (r.InstrumentId == Guid.Empty || r.Quantity <= 0 || r.Product != "NRML" || r.Segment != "FNO" ||
            r.Exchange is not ("NSE" or "BSE") || r.OrderType is not ("LIMIT" or "SL") || r.ExecutionPrice <= 0 ||
            r.ExecutionPrice % .05m != 0 || (r.OrderType == "SL" && (r.TriggerPrice is null or <= .05m ||
                r.TriggerPrice % .05m != 0 || r.Direction != Direction.Sell || r.ExecutionPrice >= r.TriggerPrice)))
            throw new ArgumentException("Upstox execution request is invalid.");
    }
}
