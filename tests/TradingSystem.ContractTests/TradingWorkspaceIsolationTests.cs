using TradingSystem.Infrastructure.MarketData;
using System.Globalization;

namespace TradingSystem.ContractTests;

public sealed class TradingWorkspaceIsolationTests
{
    [Theory]
    [InlineData("sensex", "Sensex")]
    [InlineData("natural-gas", "Natural Gas Mini Futures")]
    public void UnavailableNonNiftyMarketNeverInheritsNiftyAutomation(
        string market,
        string expectedName)
    {
        var definition = TradingMarketCatalog.Get(market);
        var observedAt = DateTimeOffset.Parse("2026-08-20T06:00:00Z", CultureInfo.InvariantCulture);

        var snapshot = EfTradingWorkspaceReader.CreateUnavailableAutomation(
            definition,
            "InstrumentUnavailable",
            $"Synchronise the Groww {expectedName} instrument before enabling the feed.",
            observedAt);

        Assert.Equal("InstrumentUnavailable", snapshot.Status);
        Assert.False(snapshot.TradingPermitted);
        Assert.Equal(0, snapshot.TradesToday);
        Assert.Equal(0m, snapshot.RealisedPnl);
        Assert.Equal(0m, snapshot.UnrealisedPnl);
        Assert.Null(snapshot.PortfolioRisk);
        var check = Assert.Single(snapshot.ReadinessChecks!);
        Assert.Contains(expectedName, check.Label);
        Assert.False(check.Ready);
    }
}
