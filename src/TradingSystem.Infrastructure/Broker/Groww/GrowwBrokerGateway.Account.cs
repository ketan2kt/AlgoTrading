using System.Globalization;
using System.Text.Json;
using TradingSystem.Application.Broker;
using TradingSystem.Domain.Trading;

namespace TradingSystem.Infrastructure.Broker.Groww;

public sealed partial class GrowwBrokerGateway : ILiveAccountEvidenceGateway
{
    // One account-wide snapshot, not one poll/subscription per index. Missing or
    // truncated arrays are errors, never interpreted as an empty/closed account.
    public async Task<LiveAccountSnapshot> ReadAccountAsync(CancellationToken token)
    {
        var orders = new Dictionary<string, LiveAccountOrder>(StringComparer.Ordinal);
        var complete = false;
        for (var page = 0; page < 20; page++)
        {
            var payload = await SendAsync<JsonElement>(HttpMethod.Get,
                $"/v1/order/list?segment=FNO&page={page}&page_size=100", null, token);
            if (!payload.TryGetProperty("order_list", out var list) || list.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Incomplete broker order-list response.");
            foreach (var row in list.EnumerateArray())
            {
                var order = ParseAccountOrder(row);
                if (orders.TryGetValue(order.Id, out var prior) && prior != order)
                    throw new InvalidDataException("Broker order changed across snapshot pages; retry reconciliation.");
                orders[order.Id] = order;
            }
            if (list.GetArrayLength() < 100) { complete = true; break; }
        }
        if (!complete) throw new InvalidDataException("Broker order-list pagination exceeded safety bound.");
        var positionsPayload = await SendAsync<JsonElement>(HttpMethod.Get, "/v1/positions/user?segment=FNO", null, token);
        if (!positionsPayload.TryGetProperty("positions", out var positions) || positions.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Incomplete broker position response.");
        var result = positions.EnumerateArray().Select(p => new LiveAccountPosition(
            Text(p, "trading_symbol"), Text(p, "exchange"), Text(p, "product"), Number(p, "quantity"))).ToArray();
        return new(orders.Values.ToArray(), result, timeProvider.GetUtcNow());
    }

    internal static LiveAccountOrder ParseAccountOrder(JsonElement row)
    {
        var side = Text(row, "transaction_type") switch
        { "BUY" => Direction.Buy, "SELL" => Direction.Sell, _ => throw new InvalidDataException("Unknown broker side.") };
        var quantity = Number(row, "quantity"); var filled = Number(row, "filled_quantity");
        if (quantity <= 0 || filled < 0 || filled > quantity) throw new InvalidDataException("Invalid broker quantities.");
        var stamp = Text(row, "created_at");
        // Broker offset-free timestamps are IST, never host-local time.
        if (!stamp.EndsWith('Z') && !stamp.Contains('+') && stamp.LastIndexOf('-') <= 9) stamp += "+05:30";
        if (!DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
            throw new InvalidDataException("Missing broker order time.");
        return new(Text(row, "groww_order_id"), row.TryGetProperty("order_reference_id", out var reference)
            ? reference.GetString() ?? "" : "", Text(row, "trading_symbol"), Text(row, "exchange"),
            Text(row, "product"), side, quantity, filled, MapState(Text(row, "order_status"), filled, quantity), at,
            row.TryGetProperty("trigger_price", out var trigger) && trigger.ValueKind == JsonValueKind.Number &&
            trigger.TryGetDecimal(out var price) ? price : null);
    }

    public async Task<ExternalExitFill> ReadExternalFillAsync(string orderId, int expectedQuantity, CancellationToken token)
    {
        var fills = new Dictionary<string, (int Units, decimal Price)>(StringComparer.Ordinal);
        var complete = false;
        for (var page = 0; page < 40; page++)
        {
            var payload = await SendAsync<JsonElement>(HttpMethod.Get,
                $"/v1/order/trades/{Uri.EscapeDataString(orderId)}?segment=FNO&page={page}&page_size=50", null, token);
            if (!payload.TryGetProperty("trade_list", out var list) || list.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Incomplete broker trades response.");
            foreach (var row in list.EnumerateArray())
            {
                if (Text(row, "groww_order_id") != orderId || Text(row, "transaction_type") != "SELL")
                    throw new InvalidDataException("External fill identity does not match.");
                var id = Text(row, "groww_trade_id"); var qty = Number(row, "quantity");
                var price = row.GetProperty("price").GetDecimal();
                if (qty <= 0 || price <= 0) throw new InvalidDataException("Invalid external execution.");
                if (fills.TryGetValue(id, out var prior) && prior != (qty, price))
                    throw new InvalidDataException("Conflicting broker trade identity.");
                fills[id] = (qty, price);
            }
            if (list.GetArrayLength() < 50) { complete = true; break; }
        }
        var units = fills.Values.Sum(x => x.Units);
        if (!complete || units != expectedQuantity || units <= 0)
            throw new InvalidDataException("External fill prices do not cover confirmed quantity.");
        return new(orderId, units, fills.Values.Sum(x => x.Units * x.Price) / units, timeProvider.GetUtcNow());
    }

    private static string Text(JsonElement row, string key) =>
        row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()! : throw new InvalidDataException("Missing broker field: " + key);
    private static int Number(JsonElement row, string key) =>
        row.TryGetProperty(key, out var value) && value.TryGetInt32(out var number)
            ? number : throw new InvalidDataException("Missing broker quantity: " + key);
}
