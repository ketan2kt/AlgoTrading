using TradingSystem.Application.Execution;
using System.Globalization;

namespace TradingSystem.UnitTests;

public sealed class HistoricalInstrumentCaptureScheduleTests
{
    [Theory]
    [InlineData("2026-11-02T03:14:59Z", "2026-11-02T03:15:00Z")]
    [InlineData("2026-11-02T03:15:00Z", "2026-11-03T03:15:00Z")]
    [InlineData("2026-11-02T12:00:00Z", "2026-11-03T03:15:00Z")]
    [InlineData("2026-11-06T12:00:00Z", "2026-11-09T03:15:00Z")]
    public void ChoosesTheNext0845IstCapture(string now, string expected)
    {
        Assert.Equal(DateTimeOffset.Parse(expected, CultureInfo.InvariantCulture),
            HistoricalInstrumentCaptureSchedule.NextPreOpenUtc(
                DateTimeOffset.Parse(now, CultureInfo.InvariantCulture)));
    }
}
