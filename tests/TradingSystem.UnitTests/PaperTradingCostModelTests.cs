using TradingSystem.Application.Execution;

namespace TradingSystem.UnitTests;

public sealed class PaperTradingCostModelTests
{
    [Fact]
    public void CalculatesEveryChargeForRoundTripOptionOrders()
    {
        var result = PaperTradingCostModel.CalculateOptionCharges([
            new(PaperOptionTransactionSide.Buy, 100m, 65),
            new(PaperOptionTransactionSide.Sell, 110m, 65)
        ]);

        Assert.Equal(40m, result.Brokerage);
        Assert.Equal(10.73m, result.SecuritiesTransactionTax);
        Assert.Equal(4.78m, result.ExchangeTransactionCharges);
        Assert.Equal(0.07m, result.InvestorProtectionFund);
        Assert.Equal(0.01m, result.SebiTurnoverFees);
        Assert.Equal(8.08m, result.GoodsAndServicesTax);
        Assert.Equal(0.20m, result.StampDuty);
        Assert.Equal(63.86m, result.Total);
        Assert.Equal("GROWW-NSE-OPTIONS-2026-04-01", result.ScheduleVersion);
    }

    [Fact]
    public void CountsPartialExitAsAnotherExecutedOrder()
    {
        var result = PaperTradingCostModel.CalculateOptionCharges([
            new(PaperOptionTransactionSide.Buy, 100m, 100),
            new(PaperOptionTransactionSide.Sell, 108m, 40),
            new(PaperOptionTransactionSide.Sell, 105m, 60)
        ]);

        Assert.Equal(60m, result.Brokerage);
        Assert.True(result.Total > 60m);
    }

    [Fact]
    public void EmptyReplayHasNoCharges()
    {
        Assert.Equal(PaperTradingCostBreakdown.Empty,
            PaperTradingCostModel.CalculateOptionCharges([]));
    }
}
