using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class LiveExecutionIntentTests
{
    [Fact]
    public void DefiniteBrokerRejectionDoesNotRemainReconciliationRequired()
    {
        var intent = NewIntent();
        intent.RequireReconciliation("Groww execution API returned 401.");

        intent.Reject(intent.LastError!, DateTimeOffset.UtcNow);

        Assert.Equal("Rejected", intent.Status);
        Assert.Equal("Groww execution API returned 401.", intent.LastError);
    }

    private static LiveExecutionIntent NewIntent() => new(Guid.NewGuid(), "SENSEX", Guid.NewGuid(),
        "SENSEX", Guid.NewGuid(), Direction.Buy, 100, 100m, 90m, 110m,
        "LE12345678", DateTimeOffset.UtcNow);
}
