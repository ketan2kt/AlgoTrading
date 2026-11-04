using TradingSystem.Application.Execution;

namespace TradingSystem.UnitTests;

public sealed class SensexFullStopReentryPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 10, 37, 0, TimeSpan.FromHours(5.5));

    [Fact]
    public void DefersRapidSameSideEntryAfterFullSizedStop() =>
        Assert.False(SensexFullStopReentryPolicy.AllowsEntry(Now, Now.AddMinutes(-4),
            "StopLossHit", -5_493m, 510.55m, 100, 10m));

    [Fact]
    public void AllowsEntryAfterNearFlatTrailingExit() =>
        Assert.True(SensexFullStopReentryPolicy.AllowsEntry(Now, Now.AddMinutes(-4),
            "StopLossHit", -278m, 510.55m, 100, 10m));

    [Fact]
    public void AllowsFreshEntryAndPostResetEntry()
    {
        Assert.True(SensexFullStopReentryPolicy.AllowsEntry(Now, null,
            null, 0m, 0m, 0, 10m));
        Assert.True(SensexFullStopReentryPolicy.AllowsEntry(Now, Now.AddMinutes(-10),
            "StopLossHit", -5_493m, 510.55m, 100, 10m));
    }
}
