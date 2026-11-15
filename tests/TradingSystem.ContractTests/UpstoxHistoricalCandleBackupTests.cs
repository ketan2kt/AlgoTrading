using System.Text.Json;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.MarketData;

namespace TradingSystem.ContractTests;

public sealed class UpstoxHistoricalCandleBackupTests
{
    [Theory]
    [InlineData("NIFTY", "NSE", "Nifty 50")]
    [InlineData("BANKNIFTY", "NSE", "Nifty Bank")]
    [InlineData("SENSEX", "BSE", "SENSEX")]
    public void IndexMappingRequiresExactExchangeAndIdentity(string market, string exchange, string name)
    {
        var instrument = new Instrument(Guid.NewGuid(), exchange, market, InstrumentSegment.Cash, InstrumentType.Index, DateTimeOffset.UtcNow);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new { instrument_type = "INDEX", segment = exchange + "_INDEX", instrument_key = exchange + "_INDEX|" + name }));
        Assert.True(UpstoxHistoricalCandleBackup.Matches(json.RootElement, instrument, market));
        Assert.False(UpstoxHistoricalCandleBackup.Matches(json.RootElement, instrument, "WRONG"));
    }

    [Fact]
    public void FuturesMappingRejectsWrongExpiryAndLot()
    {
        var instrument = new Instrument(Guid.NewGuid(), "NSE", "NIFTYFUT", InstrumentSegment.FuturesAndOptions, InstrumentType.Future, DateTimeOffset.UtcNow);
        instrument.UpdateBrokerMetadata("123", "NSE-NIFTY-20261124-FUT", new(2026, 11, 24), null, 65, .05m);
        var expiry = new DateTimeOffset(2026, 11, 24, 0, 0, 0, TimeSpan.FromHours(5.5)).ToUnixTimeMilliseconds();
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new { instrument_type = "FUT", segment = "NSE_FO", instrument_key = "NSE_FO|123", underlying_symbol = "NIFTY", lot_size = 65, expiry }));
        Assert.True(UpstoxHistoricalCandleBackup.Matches(json.RootElement, instrument, "NIFTY"));
        instrument.UpdateBrokerMetadata("123", "NSE-NIFTY-20261124-FUT", new(2026, 11, 25), null, 65, .05m);
        Assert.False(UpstoxHistoricalCandleBackup.Matches(json.RootElement, instrument, "NIFTY"));
    }

    [Fact]
    public void ParsesOnlyCompletedSessionMinutesAndRejectsMalformedPrices()
    {
        using var valid = JsonDocument.Parse("""{"status":"success","data":{"candles":[["2026-11-16T09:15:00+05:30",100,110,90,105,200,10],["2026-11-16T15:30:00+05:30",100,110,90,105,200,10]]}}""");
        var result = UpstoxHistoricalCandleBackup.Parse(valid.RootElement, new(2026, 11, 16));
        Assert.Equal(new DateTimeOffset(2026, 11, 16, 3, 45, 0, TimeSpan.Zero),
            GrowwHistoricalCandleImporter.ParseIndiaTimestamp(Assert.Single(result.Candles).SourceTimestamp));
        using var invalid = JsonDocument.Parse("""{"status":"success","data":{"candles":[["2026-11-16T09:15:00+05:30",100,90,80,105,200,10]]}}""");
        Assert.Throws<InvalidDataException>(() => UpstoxHistoricalCandleBackup.Parse(invalid.RootElement, new(2026, 11, 16)));
    }
}
