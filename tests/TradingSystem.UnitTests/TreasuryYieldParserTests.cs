using TradingSystem.Application.MarketData;

namespace TradingSystem.UnitTests;

public sealed class TreasuryYieldParserTests
{
    [Fact]
    public void TakesLatestDatedTenYearYieldAndPriorObservation()
    {
        const string csv = "Date,1 Mo,10 Yr,20 Yr\n10/26/2026,4.11,4.91,5.35\n" +
                           "10/27/2026,4.10,4.99,5.34\n10/29/2026,4.08,5.01,5.36\n";

        var yield = TreasuryYieldParser.ParseLatestTenYear(csv, new DateOnly(2026, 10, 28));

        Assert.NotNull(yield);
        Assert.Equal(new DateOnly(2026, 10, 27), yield.Date);
        Assert.Equal(4.99m, yield.Percent);
        Assert.Equal(4.91m, yield.PreviousPercent);
    }

    [Fact]
    public void MissingTenYearColumnIsUnavailable()
    {
        Assert.Null(TreasuryYieldParser.ParseLatestTenYear(
            "Date,2 Yr\n10/27/2026,4.91\n", new DateOnly(2026, 10, 28)));
    }

    [Fact]
    public void Utf8BomDoesNotHideDateColumn()
    {
        var yield = TreasuryYieldParser.ParseLatestTenYear(
            "\uFEFFDate,10 Yr\n10/27/2026,4.99\n", new DateOnly(2026, 10, 28));

        Assert.Equal(4.99m, yield?.Percent);
    }
}
