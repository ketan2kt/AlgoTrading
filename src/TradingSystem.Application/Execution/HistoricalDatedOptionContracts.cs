using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Execution;

public sealed record HistoricalMappedOptionContract(Guid InstrumentId,
    string Exchange, string TradingSymbol, string GrowwSymbol,
    InstrumentType Type, DateOnly? ExpiryDate, decimal? StrikePrice,
    int CurrentLotSize, decimal CurrentTickSize);

public static class HistoricalDatedOptionContracts
{
    // Match an archived contract to a mutable Instrument row only by its full
    // identity. Lot and tick always come from the pre-open historical master.
    // Missing/ambiguous mappings are excluded rather than guessed.
    public static IReadOnlyList<NiftyOptionContractCandidate> Resolve(
        IReadOnlyList<HistoricalInstrumentSnapshotItem> datedMaster,
        IReadOnlyList<HistoricalMappedOptionContract> current,
        string exchange, string underlying, DateOnly sessionDate)
    {
        ArgumentNullException.ThrowIfNull(datedMaster);
        ArgumentNullException.ThrowIfNull(current);
        var lookup = current.Where(value => value.Exchange == exchange)
            .GroupBy(value => (value.TradingSymbol, value.GrowwSymbol,
                value.Type, value.ExpiryDate, value.StrikePrice))
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single());
        return datedMaster.Where(value => value.Exchange == exchange &&
                value.Segment == InstrumentSegment.FuturesAndOptions &&
                value.Type is InstrumentType.CallOption or InstrumentType.PutOption &&
                value.TradingSymbol.StartsWith(underlying, StringComparison.Ordinal) &&
                value.ExpiryDate >= sessionDate &&
                value.ExpiryDate <= sessionDate.AddDays(60) &&
                value.StrikePrice is > 0m && value.LotSize > 0 && value.TickSize > 0m)
            .Select(value => (Dated: value, Mapped: lookup.GetValueOrDefault((
                value.TradingSymbol, value.GrowwSymbol, value.Type,
                value.ExpiryDate, value.StrikePrice))))
            .Where(value => value.Mapped is not null)
            .Select(value => new NiftyOptionContractCandidate(
                value.Mapped!.InstrumentId, value.Dated.TradingSymbol,
                value.Dated.Type, value.Dated.ExpiryDate!.Value,
                value.Dated.StrikePrice!.Value, value.Dated.LotSize,
                value.Dated.TickSize)).ToArray();
    }
}
