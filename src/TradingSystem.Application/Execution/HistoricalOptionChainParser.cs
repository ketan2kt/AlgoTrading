using System.Text.Json;

namespace TradingSystem.Application.Execution;

public static class HistoricalOptionChainParser
{
    private static readonly string[] Sides = ["call", "put"];
    public static IReadOnlyList<HistoricalReplayQuote> ParseQuoteObservations(
        string payload, DateTimeOffset receivedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(payload)) return [];
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (!document.RootElement.TryGetProperty("selectedStrikes", out var strikes) ||
                strikes.ValueKind != JsonValueKind.Array) return [];
            var observations = new List<HistoricalReplayQuote>();
            foreach (var strike in strikes.EnumerateArray())
            {
                if (strike.ValueKind != JsonValueKind.Object) continue;
                foreach (var side in Sides)
                {
                    if (!strike.TryGetProperty(side, out var contract) ||
                        contract.ValueKind != JsonValueKind.Object ||
                        !contract.TryGetProperty("tradingSymbol", out var symbolValue) ||
                        symbolValue.ValueKind != JsonValueKind.String) continue;
                    var symbol = symbolValue.GetString();
                    var price = Number(contract, "lastPrice");
                    if (string.IsNullOrWhiteSpace(symbol) || price <= 0m) continue;
                    var volume = contract.TryGetProperty("volume", out var volumeValue) &&
                        volumeValue.ValueKind == JsonValueKind.Number &&
                        volumeValue.TryGetInt64(out var parsedVolume) &&
                        parsedVolume >= 0 ? parsedVolume : (long?)null;
                    var openInterest = contract.TryGetProperty("openInterest",
                        out var oiValue) && oiValue.ValueKind == JsonValueKind.Number &&
                        oiValue.TryGetDecimal(out var parsedOi) && parsedOi >= 0m
                        ? parsedOi : (decimal?)null;
                    observations.Add(new HistoricalReplayQuote(symbol,
                        receivedAtUtc, price)
                    {
                        Source = "GrowwChainReceiptLtp",
                        OpenInterest = openInterest,
                        Volume = volume
                    });
                }
            }
            return observations;
        }
        catch (JsonException) { return []; }
    }

    public static IReadOnlyList<OptionOpenInterestStrike> Parse(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return [];
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (!document.RootElement.TryGetProperty("selectedStrikes", out var strikes) ||
                strikes.ValueKind != JsonValueKind.Array) return [];
            return strikes.EnumerateArray().Where(value =>
                    value.ValueKind == JsonValueKind.Object)
                .Select(value => new OptionOpenInterestStrike(
                    Number(value, "strikePrice"), SideOi(value, "call"),
                    SideOi(value, "put")))
                .Where(value => value.Strike > 0m).ToArray();
        }
        catch (JsonException) { return []; }
    }

    private static decimal SideOi(JsonElement strike, string side) =>
        strike.TryGetProperty(side, out var contract) &&
        contract.ValueKind == JsonValueKind.Object
            ? Number(contract, "openInterest") : 0m;

    private static decimal Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.Number &&
        property.TryGetDecimal(out var number) ? number : 0m;
}
