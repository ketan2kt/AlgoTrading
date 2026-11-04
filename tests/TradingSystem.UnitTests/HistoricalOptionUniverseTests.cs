using TradingSystem.Application.Execution;

namespace TradingSystem.UnitTests;

public sealed class HistoricalOptionUniverseTests
{
    [Fact]
    public void KeepsBothSidesWithinObservedSessionCorridor()
    {
        var selected = HistoricalOptionUniverse.Select([
            "NSE-NIFTY-03Nov26-22000-CE",
            "NSE-NIFTY-03Nov26-22000-PE",
            "NSE-NIFTY-03Nov26-23000-CE",
            "NSE-NIFTY-10Nov26-22000-CE",
            "NSE-BANKNIFTY-03Nov26-22000-CE",
            "NSE-NIFTY-03Nov26-22500-FUT",
            "NSE-NIFTY-03Nov26-bad-PE"
        ], "NSE", "NIFTY", new DateOnly(2026, 11, 3), 21900m, 22100m, 100m);

        Assert.Equal([
            "NSE-NIFTY-03Nov26-22000-CE",
            "NSE-NIFTY-03Nov26-22000-PE"
        ], selected);
    }

    [Fact]
    public void AListedContractWithoutBarsIsNotAutomaticallyReplayEligible()
    {
        var selected = HistoricalOptionUniverse.Select([
            "BSE-SENSEX-29Oct26-74000-CE"
        ], "BSE", "SENSEX", new DateOnly(2026, 10, 29), 73500m, 74200m, 1500m);

        Assert.Single(selected);
        // The selection is only an archive fetch list; a separate as-of
        // historical-bar check is required before a strategy may select it.
    }
}
