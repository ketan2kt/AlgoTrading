using TradingSystem.Application.Diagnostics;

namespace TradingSystem.UnitTests;

public sealed class DailyAnalysisAvailabilityPolicyTests
{
    [Fact]
    public void CurrentDayReportIsUnavailableBefore1515Ist()
    {
        var today = new DateOnly(2026, 10, 28);
        var before = DailyAnalysisAvailabilityPolicy.AvailableAtUtc(today).AddTicks(-1);

        Assert.False(DailyAnalysisAvailabilityPolicy.IsAvailable(today, today, before));
    }

    [Fact]
    public void CurrentDayReportIsAvailableAt1515Ist()
    {
        var today = new DateOnly(2026, 10, 28);
        var atAvailability = DailyAnalysisAvailabilityPolicy.AvailableAtUtc(today);

        Assert.True(DailyAnalysisAvailabilityPolicy.IsAvailable(today, today, atAvailability));
    }

    [Fact]
    public void PriorSessionReportRemainsAvailableAtAnyTime()
    {
        var today = new DateOnly(2026, 10, 28);

        Assert.True(DailyAnalysisAvailabilityPolicy.IsAvailable(today.AddDays(-1), today,
            DateTimeOffset.MinValue));
    }
}
