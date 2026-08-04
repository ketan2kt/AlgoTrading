using TradingSystem.Application.MarketData;

namespace TradingSystem.UnitTests;

public sealed class FuturesFeedHealthMonitorTests
{
    [Fact]
    public void FailureAndRecoveryAreReportedWithoutRetainingTheErrorAsCurrent()
    {
        var monitor = new FuturesFeedHealthMonitor();
        var failure = new DateTimeOffset(2026, 8, 4, 5, 0, 0, TimeSpan.Zero);
        var recovery = failure.AddMinutes(1);

        monitor.RecordFailure(failure, "MALFORMED_RESPONSE", "Observed five fields.");
        var failed = monitor.GetCurrent();
        Assert.False(failed.Available);
        Assert.Equal("MALFORMED_RESPONSE", failed.ErrorCode);

        monitor.RecordSuccess(recovery);
        var healthy = monitor.GetCurrent();
        Assert.True(healthy.Available);
        Assert.Equal(recovery, healthy.LastSuccessUtc);
        Assert.Null(healthy.ErrorCode);
        Assert.Null(healthy.ErrorDetail);
    }
}
