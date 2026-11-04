using TradingSystem.Application.Execution;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class HistoricalInstrumentMasterFingerprintTests
{
    [Fact]
    public void ReorderedMasterHasSameFingerprintButChangedLotDoesNot()
    {
        var items = new[]
        {
            new HistoricalInstrumentSnapshotItem("NSE", "NIFTY",
                "NSE-NIFTY", InstrumentSegment.Cash, InstrumentType.Index,
                null, null, 1, 0m),
            new HistoricalInstrumentSnapshotItem("NSE", "NIFTY22000CE",
                "NSE-NIFTY-03Nov26-22000-CE", InstrumentSegment.FuturesAndOptions,
                InstrumentType.CallOption, new DateOnly(2026, 11, 3),
                22000m, 75, .05m)
        };
        var first = HistoricalInstrumentMasterFingerprint.Create(items);
        var reversed = HistoricalInstrumentMasterFingerprint.Create(
            items.Reverse().ToArray());
        var changed = HistoricalInstrumentMasterFingerprint.Create([
            items[0], items[1] with { LotSize = 65 }
        ]);

        Assert.Equal(first.Fingerprint, reversed.Fingerprint);
        Assert.Equal(first.PayloadJson, reversed.PayloadJson);
        Assert.NotEqual(first.Fingerprint, changed.Fingerprint);
        var sameNumericValue = HistoricalInstrumentMasterFingerprint.Create([
            items[0], items[1] with { TickSize = .0500m }
        ]);
        Assert.Equal(first.Fingerprint, sameNumericValue.Fingerprint);
        Assert.Equal(2, first.ContractCount);
    }

    [Fact]
    public void AmbiguousContractIdentityFailsClosed()
    {
        var item = new HistoricalInstrumentSnapshotItem("NSE", "NIFTY22000CE",
            "NSE-NIFTY-03Nov26-22000-CE", InstrumentSegment.FuturesAndOptions,
            InstrumentType.CallOption, new DateOnly(2026, 11, 3),
            22000m, 75, .05m);
        Assert.Throws<ArgumentException>(() =>
            HistoricalInstrumentMasterFingerprint.Create([item, item]));
    }
}
