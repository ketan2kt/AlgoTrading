using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Execution;

public sealed record OptionOpenInterestStrike(
    decimal Strike,
    decimal CallOpenInterest,
    decimal PutOpenInterest);

public sealed record OptionOpenInterestEntryDecision(
    bool Permitted,
    bool Available,
    decimal? BarrierStrike,
    string? BarrierKind,
    decimal? RoomToRisk,
    decimal? OpenInterestToMedian,
    decimal? OpenInterestConcentration,
    decimal? OpenInterestChangePercent,
    IReadOnlyList<string> Reasons);

/// <summary>
/// Prevents an index-option entry from aiming directly into a nearby, concentrated
/// option-OI barrier. A call wall is resistance for an underlying buy/CE thesis; a put
/// wall is support for an underlying sell/PE thesis. Missing chain data is reported but
/// never replaced with invented values.
/// </summary>
public static class OptionOpenInterestEntryPolicy
{
    public const decimal MinimumRoomToRisk = 1.25m;
    public const decimal MaximumRelevantDistanceRisk = 2.50m;
    public const decimal MinimumOiToMedian = 1.50m;
    public const decimal MinimumConcentration = .12m;
    public const decimal DominantConcentration = .22m;
    public const decimal MinimumBuildUpPercent = .10m;
    public const decimal MinimumBuildUpOiToMedian = 1.25m;
    public const decimal MinimumBuildUpConcentration = .10m;

    public static OptionOpenInterestEntryDecision Evaluate(
        Direction direction,
        decimal underlyingPrice,
        decimal underlyingRiskDistance,
        IReadOnlyList<OptionOpenInterestStrike> current,
        IReadOnlyList<OptionOpenInterestStrike>? previous = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (direction is not (Direction.Buy or Direction.Sell))
            throw new ArgumentOutOfRangeException(nameof(direction));
        if (underlyingPrice <= 0m || underlyingRiskDistance <= 0m || current.Count == 0)
            return Unavailable("A fresh option-chain OI assessment is unavailable; no OI value was inferred.");

        var useCalls = direction == Direction.Buy;
        var side = current
            .Where(value => useCalls ? value.Strike >= underlyingPrice : value.Strike <= underlyingPrice)
            .Select(value => new
            {
                Point = value,
                Oi = useCalls ? value.CallOpenInterest : value.PutOpenInterest,
                Distance = Math.Abs(value.Strike - underlyingPrice)
            })
            .Where(value => value.Oi > 0m &&
                            value.Distance <= underlyingRiskDistance * MaximumRelevantDistanceRisk)
            .ToArray();
        if (side.Length == 0)
            return Unavailable("No positive OI exists on the relevant side inside the 2.50R decision horizon.");

        var allSideOi = current
            .Select(value => useCalls ? value.CallOpenInterest : value.PutOpenInterest)
            .Where(value => value > 0m).OrderBy(value => value).ToArray();
        if (allSideOi.Length == 0)
            return Unavailable("The option chain contains no positive OI for the required option side.");
        var median = Median(allSideOi);
        var total = allSideOi.Sum();
        var previousByStrike = (previous ?? [])
            .GroupBy(value => value.Strike)
            .ToDictionary(group => group.Key, group => group.Last());

        var barriers = side.Select(value =>
        {
            var ratio = median <= 0m ? 0m : value.Oi / median;
            var concentration = total <= 0m ? 0m : value.Oi / total;
            decimal? change = null;
            if (previousByStrike.TryGetValue(value.Point.Strike, out var prior))
            {
                var priorOi = useCalls ? prior.CallOpenInterest : prior.PutOpenInterest;
                if (priorOi > 0m) change = (value.Oi - priorOi) / priorOi;
            }
            var establishedWall = ratio >= MinimumOiToMedian &&
                                  concentration >= MinimumConcentration ||
                                  concentration >= DominantConcentration;
            var buildingWall = change >= MinimumBuildUpPercent &&
                               ratio >= MinimumBuildUpOiToMedian &&
                               concentration >= MinimumBuildUpConcentration;
            return new
            {
                value.Point.Strike,
                value.Oi,
                value.Distance,
                Ratio = ratio,
                Concentration = concentration,
                Change = change,
                IsBarrier = establishedWall || buildingWall,
                IsBuilding = buildingWall
            };
        }).Where(value => value.IsBarrier)
          .OrderBy(value => value.Distance)
          .ThenByDescending(value => value.Oi)
          .FirstOrDefault();

        if (barriers is null)
            return new(true, true, null, null, null, null, null, null,
                [$"No concentrated {(useCalls ? "call resistance" : "put support")} OI wall exists inside the 2.50R decision horizon."]);

        var roomToRisk = barriers.Distance / underlyingRiskDistance;
        var kind = useCalls ? "CallResistance" : "PutSupport";
        var buildUp = barriers.Change is { } changeValue
            ? $" OI change from the previous snapshot is {changeValue:P0}."
            : " No earlier matching snapshot is available for an OI-change claim.";
        var evidence = $"{kind} at {barriers.Strike:F0}: {barriers.Oi:F0} OI, " +
                       $"{barriers.Ratio:F2}x the side median, {barriers.Concentration:P0} of visible side OI; " +
                       $"room is {roomToRisk:F2}R.{buildUp}";
        if (roomToRisk < MinimumRoomToRisk)
            return new(false, true, barriers.Strike, kind, roomToRisk, barriers.Ratio,
                barriers.Concentration, barriers.Change,
                [evidence, $"No trade: a minimum {MinimumRoomToRisk:F2}R of room is required before the OI barrier."]);

        return new(true, true, barriers.Strike, kind, roomToRisk, barriers.Ratio,
            barriers.Concentration, barriers.Change,
            [evidence, "The OI barrier leaves sufficient pre-barrier room for the planned risk."]);
    }

    private static OptionOpenInterestEntryDecision Unavailable(string reason) =>
        new(true, false, null, null, null, null, null, null, [reason]);

    private static decimal Median(decimal[] values)
    {
        var middle = values.Length / 2;
        return values.Length % 2 == 1 ? values[middle] : (values[middle - 1] + values[middle]) / 2m;
    }
}
