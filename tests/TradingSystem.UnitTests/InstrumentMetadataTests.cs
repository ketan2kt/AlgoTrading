using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class InstrumentMetadataTests
{
    [Fact]
    public void GrowwMetadataUpdateValidatesAndAppliesReferenceData()
    {
        var instrument = new Instrument(
            Guid.NewGuid(),
            "NSE",
            "NIFTY25AUGFUT",
            InstrumentSegment.FuturesAndOptions,
            InstrumentType.Future,
            DateTimeOffset.UtcNow);

        instrument.UpdateBrokerMetadata(
            "35241",
            "NSE-NIFTY-27Aug26-FUT",
            new DateOnly(2026, 8, 27),
            null,
            75,
            0.05m);

        Assert.Equal("35241", instrument.ExchangeToken);
        Assert.Equal("NSE-NIFTY-27Aug26-FUT", instrument.GrowwSymbol);
        Assert.Equal(75, instrument.LotSize);
        Assert.Equal(0.05m, instrument.TickSize);
        Assert.True(instrument.IsActive);
    }

    [Fact]
    public void InvalidLotAndTickMetadataIsRejected()
    {
        var instrument = new Instrument(
            Guid.NewGuid(),
            "NSE",
            "NIFTY",
            InstrumentSegment.Cash,
            InstrumentType.Index,
            DateTimeOffset.UtcNow);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            instrument.UpdateBrokerMetadata("26000", "NSE-NIFTY", null, null, 0, 0.05m));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            instrument.UpdateBrokerMetadata("26000", "NSE-NIFTY", null, null, 1, 0m));
    }
}
