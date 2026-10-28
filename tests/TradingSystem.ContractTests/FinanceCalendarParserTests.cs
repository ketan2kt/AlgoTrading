using TradingSystem.Infrastructure.MarketData;

namespace TradingSystem.ContractTests;

public sealed class FinanceCalendarParserTests
{
    private static readonly DateTimeOffset Capture =
        new(2026, 10, 29, 3, 15, 0, TimeSpan.Zero);

    [Fact]
    public void ConfirmedUpcomingEventIsTimestampedInIst()
    {
        var evidence = FinanceCalendarParser.Parse("""
            {"events":[{"name":"US GDP","country":"US","impact":"high",
              "status":"confirmed","release_at_utc":"2026-10-29T18:00:00+05:30",
              "last_verified_at":"2026-10-28T23:11:35+05:30","data_stale":false}]}
            """, Capture);

        Assert.NotNull(evidence);
        Assert.Equal("Finance Calendar", evidence.Source);
        Assert.Contains("29 Oct 18:00 IST", evidence.Value);
        Assert.NotNull(evidence.SourceAtUtc);
    }

    [Fact]
    public void StaleOrUnverifiedEventsDoNotBecomePublishedEvidence()
    {
        var evidence = FinanceCalendarParser.Parse("""
            {"events":[{"name":"US GDP","country":"US","impact":"high",
              "status":"confirmed","release_at_utc":"2026-10-29T18:00:00+05:30",
              "data_stale":true}]}
            """, Capture);

        Assert.Null(evidence);
    }

    [Fact]
    public void OutOfHorizonEventDoesNotAppearAsUpcoming()
    {
        var evidence = FinanceCalendarParser.Parse("""
            {"events":[{"name":"US GDP","country":"US","impact":"high",
              "status":"confirmed","release_at_utc":"2026-11-10T18:00:00+05:30",
              "last_verified_at":"2026-10-28T23:11:35+05:30","data_stale":false}]}
            """, Capture);

        Assert.NotNull(evidence);
        Assert.Contains("No confirmed", evidence.Value);
    }
}
