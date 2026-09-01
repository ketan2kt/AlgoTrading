using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class LiveExecutionIntentTests
{
    [Fact]
    public void IntentMovesFromPendingToBrokerProtected()
    {
        var now = new DateTimeOffset(2026, 9, 1, 4, 0, 0, TimeSpan.Zero);
        var intent = Create(now);

        intent.Submitted("groww-1", now.AddSeconds(1));
        intent.Filled(325, 100m);
        intent.BeginProtection();
        intent.Protected("oco-1", now.AddSeconds(2));

        Assert.Equal("Protected", intent.Status);
        Assert.Equal(325, intent.FilledQuantity);
        Assert.Equal(100m, intent.AverageFillPrice);
        Assert.Equal("oco-1", intent.ProtectionId);
    }

    [Fact]
    public void PartialFillRemainsExplicitUntilProtected()
    {
        var intent = Create(DateTimeOffset.UtcNow);
        intent.Submitted("groww-1", DateTimeOffset.UtcNow);

        intent.Filled(65, 100m);

        Assert.Equal("PartiallyFilled", intent.Status);
    }

    [Fact]
    public void UnknownBrokerOutcomeBlocksReuseUntilReconciliation()
    {
        var intent = Create(DateTimeOffset.UtcNow);

        intent.RequireReconciliation("Submission outcome unknown.");

        Assert.Equal("ReconciliationRequired", intent.Status);
        Assert.Equal("Submission outcome unknown.", intent.LastError);
    }

    private static LiveExecutionIntent Create(DateTimeOffset now) => new(Guid.NewGuid(),
        "NIFTY", Guid.NewGuid(), "NIFTY", Guid.NewGuid(), Direction.Buy, 325,
        100m, 90m, 110m, "LE1234567890123456", now);
}
