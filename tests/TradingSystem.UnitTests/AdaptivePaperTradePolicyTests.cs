using TradingSystem.Application.Risk;

namespace TradingSystem.UnitTests;

public sealed class AdaptivePaperTradePolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PermitsWithinEvidenceBasedAllocation()
    {
        var result = AdaptivePaperTradePolicy.Evaluate(1, 4, 0, 2, null, Now, 30);

        Assert.True(result.Permitted);
        Assert.Empty(result.Reasons);
    }

    [Fact]
    public void RejectsExhaustedDailyAllocation()
    {
        var result = AdaptivePaperTradePolicy.Evaluate(4, 4, 0, 2, null, Now, 30);

        Assert.False(result.Permitted);
        Assert.Contains(result.Reasons, reason => reason.Contains("daily research allocation"));
    }

    [Fact]
    public void RejectsConsecutiveLossesAndActiveCooldown()
    {
        var result = AdaptivePaperTradePolicy.Evaluate(2, 4, 2, 2, Now.AddMinutes(-10), Now, 30);

        Assert.False(result.Permitted);
        Assert.Contains(result.Reasons, reason => reason.Contains("consecutive net losses"));
        Assert.Contains(result.Reasons, reason => reason.Contains("cooldown"));
    }

    [Fact]
    public void CooldownExpiresAtConfiguredBoundary()
    {
        var result = AdaptivePaperTradePolicy.Evaluate(1, 4, 0, 2, Now.AddMinutes(-30), Now, 30);

        Assert.True(result.Permitted);
    }
}
