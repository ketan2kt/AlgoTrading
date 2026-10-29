using TradingSystem.Application.Execution;

namespace TradingSystem.UnitTests;

public sealed class PaperPnlSummaryCalculatorTests
{
    [Fact]
    public void IncludesBothBankMarketsInAllMarketNetTotals()
    {
        PaperPnlObservation[] rows =
        [
            new("nifty", 900m, 20m),
            new("sensex", -100m, 10m),
            new("bank-nifty", 500m, 15m),
            new("bankex", -200m, 5m)
        ];

        var result = PaperPnlSummaryCalculator.Summarise(rows);

        Assert.Equal(["nifty", "sensex", "bank-nifty", "bankex"],
            result.Select(value => value.Market));
        Assert.Equal(4, result.Sum(value => value.Trades));
        Assert.Equal(1100m, result.Sum(value => value.NetPnl));
        Assert.Equal(50m, result.Sum(value => value.Charges));
    }

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

    [Fact]
    public void CountsHeroZeroLegsAsTradesAndKeepsTheirCombinedNetResult()
    {
        PaperPnlObservation[] rows =
        [
            new("nifty", 27279.08m, 120.92m),
            new("nifty", -6095.85m, 94.15m)
        ];

        var nifty = Assert.Single(PaperPnlSummaryCalculator.Summarise(rows));

        Assert.Equal(2, nifty.Trades);
        Assert.Equal(1, nifty.Wins);
        Assert.Equal(1, nifty.Losses);
        Assert.Equal(27400m, nifty.GrossProfit);
        Assert.Equal(-6001.70m, nifty.GrossLoss);
        Assert.Equal(215.07m, nifty.Charges);
        Assert.Equal(21183.23m, nifty.NetPnl);
    }
}
