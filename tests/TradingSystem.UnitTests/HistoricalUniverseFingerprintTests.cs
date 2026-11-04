using TradingSystem.Application.Execution;

namespace TradingSystem.UnitTests;

public sealed class HistoricalUniverseFingerprintTests
{
    [Fact]
    public void StableForOrderAndChangesForRevisions()
    {
        var date = new DateOnly(2026, 10, 29);
        var expiry = date.AddDays(5);
        var start = new DateTimeOffset(2026, 10, 29, 3, 45, 0,
            TimeSpan.Zero);
        HistoricalUniverseIndexRangePoint[] range =
        [
            new(start, 22000m, 22020m),
            new(start.AddMinutes(1), 22015m, 22030m)
        ];
        var baseline = HistoricalUniverseFingerprint.Compute(date, "nifty", expiry,
            ["CE", "PE"], range);
        Assert.Equal(baseline, HistoricalUniverseFingerprint.Compute(date,
            "nifty", expiry, ["PE", "CE"], range.Reverse().ToArray()));
        Assert.NotEqual(baseline, HistoricalUniverseFingerprint.Compute(date,
            "nifty", expiry, ["CE", "PE", "CE2"], range));
        Assert.NotEqual(baseline, HistoricalUniverseFingerprint.Compute(date,
            "nifty", expiry, ["CE", "PE"],
            [range[0], range[1] with { High = 22035m }]));
        Assert.Throws<ArgumentException>(() => HistoricalUniverseFingerprint.Compute(
            date, "nifty", expiry, ["CE", "PE"], [range[0], range[0]]));
        Assert.Throws<ArgumentException>(() => HistoricalUniverseFingerprint.Compute(
            date, "nifty", expiry, ["CE", "CE"], range));
    }
}
