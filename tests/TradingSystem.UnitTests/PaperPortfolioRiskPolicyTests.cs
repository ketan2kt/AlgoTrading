using TradingSystem.Application.Risk;

namespace TradingSystem.UnitTests;

public sealed class PaperPortfolioRiskPolicyTests
{
    [Fact]
    public void UncappedPaperUsesSavedLotsDespiteLossCapitalAndPositionLimits()
    {
        var result = PaperPortfolioRiskPolicy.EvaluateUncappedPaper(Valid() with
        {
            MaximumLots = 10, LotSize = 65, EntryPrice = 100m, StopLoss = 80m,
            MaximumRiskPerTrade = 5000m, MaximumPortfolioExposure = 1000m,
            ExistingCapitalExposure = 200000m, DailyRealisedPnl = -20000m,
            DailyUnrealisedPnl = -10000m, OpenPositions = 20, MaximumOpenPositions = 1
        });
        Assert.True(result.Approved);
        Assert.Equal(650, result.ApprovedQuantity);
        Assert.Equal(13000m, result.RiskAmount);
    }

    [Fact]
    public void UncappedPaperStillHonoursEmergencyStopAndFreshData()
    {
        Assert.False(PaperPortfolioRiskPolicy.EvaluateUncappedPaper(Valid() with
            { KillSwitchActive = true }).Approved);
        Assert.False(PaperPortfolioRiskPolicy.EvaluateUncappedPaper(Valid() with
            { MarketDataFresh = false }).Approved);
    }

    [Fact]
    public void FloorsFiveLotRequestToRiskBudgetInWholeLots()
    {
        var result = PaperPortfolioRiskPolicy.Evaluate(Valid() with
        {
            EntryPrice = 100m,
            StopLoss = 80m,
            LotSize = 65,
            MaximumLots = 5,
            MaximumRiskPerTrade = 5_000m
        });

        Assert.True(result.Approved);
        Assert.Equal(195, result.ApprovedQuantity);
        Assert.Equal(3_900m, result.RiskAmount);
    }

    [Fact]
    public void CountsOpenLossTowardDailyStop()
    {
        var result = PaperPortfolioRiskPolicy.Evaluate(Valid() with
        {
            DailyRealisedPnl = -3_000m,
            DailyUnrealisedPnl = -2_100m,
            MaximumDailyLoss = 5_000m
        });

        Assert.False(result.Approved);
        Assert.Contains(result.RejectionReasons, reason => reason.Contains("Daily loss"));
    }

    [Fact]
    public void DailyLossOverrideAllowsPaperEntryBeyondDailyStop()
    {
        var result = PaperPortfolioRiskPolicy.Evaluate(Valid() with
        {
            DailyRealisedPnl = -5_100m,
            MaximumDailyLoss = 5_000m,
            DailyLossLimitOverridden = true
        });

        Assert.True(result.Approved);
        Assert.DoesNotContain(result.RejectionReasons, reason => reason.Contains("Daily loss"));
    }

    [Fact]
    public void AggregateExposureCannotExceedPortfolioLimit()
    {
        var result = PaperPortfolioRiskPolicy.Evaluate(Valid() with
        {
            EntryPrice = 100m,
            ExistingCapitalExposure = 195_000m,
            MaximumPortfolioExposure = 200_000m,
            LotSize = 65
        });

        Assert.False(result.Approved);
        Assert.Equal(0, result.ApprovedQuantity);
    }

    private static PaperPortfolioRiskInput Valid() => new(
        100m, 90m, 65, 5, 5_000m, 200_000m, 0m,
        0m, 0m, 5_000m, 0, 8, false, true, true);
}
