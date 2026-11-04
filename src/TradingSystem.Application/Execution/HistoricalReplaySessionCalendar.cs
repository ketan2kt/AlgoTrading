namespace TradingSystem.Application.Execution;

public static class HistoricalReplaySessionCalendar
{
    // Calendar permission only: actual quote/universe coverage is still checked.
    public static bool IsCompletedDate(DateOnly date, DateTimeOffset now)
    {
        var local = now.ToOffset(TimeSpan.FromHours(5.5));
        var today = DateOnly.FromDateTime(local.DateTime);
        return date < today || date == today && TimeOnly.FromDateTime(local.DateTime) >= new TimeOnly(15, 45);
    }
}
