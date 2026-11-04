using TradingSystem.Application.Execution;

namespace TradingSystem.UnitTests;

public sealed class BankIndexOptionAdmissionPolicyTests
{
    [Fact]
    public void TwoSidedQuoteUsesOfferAndSymmetricPremiumRisk()
    {
        var decision = BankIndexOptionAdmissionPolicy.Evaluate(
            99m, 99.5m, 100m, 30, .05m, 10m);

        Assert.True(decision.Permitted);
        Assert.False(decision.SpreadRejected);
        Assert.Equal(100m, decision.Entry);
        Assert.Equal(90m, decision.Stop);
        Assert.Equal(110m, decision.Target);
    }

    [Fact]
    public void ProductionFallbackAndSpreadAuditRemainUnchanged()
    {
        var fallback = BankIndexOptionAdmissionPolicy.Evaluate(
            100m, null, null, 30, .05m, 10m);
        Assert.True(fallback.Permitted);
        Assert.Equal(100m, fallback.Entry);

        var wide = BankIndexOptionAdmissionPolicy.Evaluate(
            100m, 97m, 100m, 30, .05m, 10m);
        Assert.False(wide.Permitted);
        Assert.True(wide.SpreadRejected);
    }

    [Fact]
    public void InvalidGeometryOrLotDoesNotCreatePosition()
    {
        Assert.False(BankIndexOptionAdmissionPolicy.Evaluate(
            100m, 99m, 100m, 0, .05m, 10m).Permitted);
        Assert.False(BankIndexOptionAdmissionPolicy.Evaluate(
            .05m, .05m, .05m, 30, .05m, 10m).Permitted);
    }
}
