using TradingSystem.Application.Execution;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class HistoricalDatedOptionContractsTests
{
    [Fact]
    public void UsesPreOpenLotAndTickRatherThanCurrentMutableMetadata()
    {
        var expiry = new DateOnly(2026, 11, 3);
        var archived = new HistoricalInstrumentSnapshotItem("NSE", "NIFTY22000CE",
            "NSE-NIFTY-03Nov26-22000-CE", InstrumentSegment.FuturesAndOptions,
            InstrumentType.CallOption, expiry, 22000m, 75, .05m);
        var id = Guid.NewGuid();
        var current = new HistoricalMappedOptionContract(id, "NSE",
            archived.TradingSymbol, archived.GrowwSymbol, archived.Type,
            expiry, 22000m, 65, .10m);

        var selected = Assert.Single(HistoricalDatedOptionContracts.Resolve(
            [archived], [current], "NSE", "NIFTY", new DateOnly(2026, 11, 2)));

        Assert.Equal(id, selected.InstrumentId);
        Assert.Equal(75, selected.LotSize);
        Assert.Equal(.05m, selected.TickSize);
    }

    [Fact]
    public void MissingOrAmbiguousHistoricalIdentityIsNotGuessed()
    {
        var expiry = new DateOnly(2026, 11, 3);
        var archived = new HistoricalInstrumentSnapshotItem("NSE", "NIFTY22000CE",
            "NSE-NIFTY-03Nov26-22000-CE", InstrumentSegment.FuturesAndOptions,
            InstrumentType.CallOption, expiry, 22000m, 75, .05m);
        var wrong = new HistoricalMappedOptionContract(Guid.NewGuid(), "NSE",
            archived.TradingSymbol, "DIFFERENT", archived.Type,
            expiry, 22000m, 75, .05m);
        Assert.Empty(HistoricalDatedOptionContracts.Resolve([archived], [wrong],
            "NSE", "NIFTY", new DateOnly(2026, 11, 2)));
        var exact = wrong with { GrowwSymbol = archived.GrowwSymbol };
        Assert.Empty(HistoricalDatedOptionContracts.Resolve([archived],
            [exact, exact with { InstrumentId = Guid.NewGuid() }], "NSE",
            "NIFTY", new DateOnly(2026, 11, 2)));
    }
}
