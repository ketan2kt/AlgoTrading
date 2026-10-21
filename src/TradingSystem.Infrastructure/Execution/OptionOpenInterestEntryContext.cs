using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TradingSystem.Application.Execution;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.Infrastructure.Execution;

internal static class OptionOpenInterestEntryContext
{
    private static readonly TimeSpan MaximumSnapshotAge = TimeSpan.FromMinutes(3);

    public static async Task<OptionOpenInterestEntryDecision> EvaluateAsync(
        TradingDbContext db,
        Guid underlyingInstrumentId,
        Direction direction,
        decimal underlyingPrice,
        decimal underlyingRiskDistance,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var snapshots = await db.OptionChainSnapshots.AsNoTracking()
            .Where(value => value.UnderlyingInstrumentId == underlyingInstrumentId &&
                            value.SourceTimestampUtc >= nowUtc - MaximumSnapshotAge)
            .OrderByDescending(value => value.SourceTimestampUtc)
            .Take(2)
            .Select(value => new { value.SourceTimestampUtc, value.ExpiryDate, value.PayloadJson })
            .ToArrayAsync(cancellationToken);
        if (snapshots.Length == 0)
            return OptionOpenInterestEntryPolicy.Evaluate(direction, underlyingPrice,
                underlyingRiskDistance, []);

        var current = Read(snapshots[0].PayloadJson);
        var previous = snapshots.Skip(1)
            .FirstOrDefault(value => value.ExpiryDate == snapshots[0].ExpiryDate);
        var decision = OptionOpenInterestEntryPolicy.Evaluate(direction, underlyingPrice,
            underlyingRiskDistance, current, previous is null ? [] : Read(previous.PayloadJson));
        return decision with
        {
            Reasons =
            [
                $"Official option-chain snapshot age is {(nowUtc - snapshots[0].SourceTimestampUtc).TotalSeconds:F0} second(s).",
                .. decision.Reasons
            ]
        };
    }

    private static OptionOpenInterestStrike[] Read(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("selectedStrikes", out var strikes) ||
                strikes.ValueKind != JsonValueKind.Array) return [];
            return strikes.EnumerateArray().Select(strike => new OptionOpenInterestStrike(
                Decimal(strike, "strikePrice"),
                ContractOi(strike, "call"),
                ContractOi(strike, "put"))).Where(value => value.Strike > 0m).ToArray();
        }
        catch (JsonException)
        {
            return [];
        }

        static decimal ContractOi(JsonElement strike, string side) =>
            strike.TryGetProperty(side, out var contract) && contract.ValueKind == JsonValueKind.Object
                ? Decimal(contract, "openInterest")
                : 0m;
        static decimal Decimal(JsonElement element, string property) =>
            element.TryGetProperty(property, out var value) && value.TryGetDecimal(out var number)
                ? number
                : 0m;
    }
}
