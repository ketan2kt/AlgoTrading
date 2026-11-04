using TradingSystem.Application.Execution;

namespace TradingSystem.UnitTests;

public sealed class HistoricalFutureUniverseTests
{
    [Fact]
    public void SelectsOnlyTheVerifiedFutureForTheRequestedExpiry()
    {
        var symbols = new[]
        {
            "NSE-NIFTY-03Nov26-22000-CE",
            "NSE-NIFTY-26Nov26-FUT",
            "NSE-BANKNIFTY-26Nov26-FUT"
        };
        Assert.Null(HistoricalFutureUniverse.SelectForExpiry(symbols,
            "NSE", "NIFTY", new DateOnly(2026, 11, 3)));
        Assert.Equal("NSE-NIFTY-26Nov26-FUT",
            HistoricalFutureUniverse.SelectForExpiry(symbols,
                "NSE", "NIFTY", new DateOnly(2026, 11, 26)));
    }
}
