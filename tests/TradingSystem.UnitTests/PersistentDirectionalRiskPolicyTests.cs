using TradingSystem.Application.Execution;

namespace TradingSystem.UnitTests;

public sealed class PersistentDirectionalRiskPolicyTests
{
    [Fact]
    public void NormalTradeKeepsImmediatePremiumStop()
    {
        var result = PersistentDirectionalStopPolicy.Evaluate(false, true,
            TimeSpan.FromMinutes(1), 1m, false);

        Assert.True(result.ShouldExit);
        Assert.False(result.Deferred);
    }

    [Fact]
    public void DefersEarlyPremiumNoiseForPersistentPaperThesis()
    {
        var result = PersistentDirectionalStopPolicy.Evaluate(true, true,
            TimeSpan.FromSeconds(45), 1.05m, false);

        Assert.False(result.ShouldExit);
        Assert.True(result.Deferred);
    }

    [Theory]
    [InlineData(true, 1.1)]
    [InlineData(false, 2.0)]
    public void ExitsOnUnderlyingInvalidationOrCatastrophicBoundary(
        bool invalidated, decimal adverseRiskMultiple)
    {
        var result = PersistentDirectionalStopPolicy.Evaluate(true, true,
            TimeSpan.FromMinutes(6), adverseRiskMultiple, invalidated);

        Assert.True(result.ShouldExit);
        Assert.False(result.Deferred);
    }

    [Fact]
    public void BlocksRapidOppositeSideWhipsaw()
    {
        var now = new DateTimeOffset(2026, 10, 19, 5, 0, 0, TimeSpan.Zero);
        var result = DirectionFlipCooldownPolicy.Evaluate(now.AddMinutes(-25), now);

        Assert.False(result.Permitted);
    }

    [Fact]
    public void AllowsOppositeSideAfterResetWindow()
    {
        var now = new DateTimeOffset(2026, 10, 19, 5, 0, 0, TimeSpan.Zero);
        var result = DirectionFlipCooldownPolicy.Evaluate(now.AddMinutes(-30), now);

        Assert.True(result.Permitted);
    }
}
