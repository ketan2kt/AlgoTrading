using TradingSystem.Application.Execution;

namespace TradingSystem.UnitTests;

public sealed class PaperPnlSummaryCalculatorTests
{
    [Fact]
    public void GroupsMarketsAndPreservesNetPnlIncludingCharges()
    {
        PaperPnlObservation[] rows =
        [
            new("nifty", 900m, 100m),
            new("nifty", -550m, 50m),
            new("sensex", 1200m, 80m),
            new("natural-gas", -300m, 25m)
        ];

        var result = PaperPnlSummaryCalculator.Summarise(rows);

        Assert.Equal(["nifty", "sensex", "natural-gas"], result.Select(value => value.Market));
        var nifty = result[0];
        Assert.Equal(2, nifty.Trades);
        Assert.Equal(1, nifty.Wins);
        Assert.Equal(1, nifty.Losses);
        Assert.Equal(350m, nifty.NetPnl);
        Assert.Equal(150m, nifty.Charges);
        Assert.Equal(1250m, result.Sum(value => value.NetPnl));
    }
}
