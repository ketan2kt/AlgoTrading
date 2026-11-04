namespace TradingSystem.Application.Execution;

public static class HistoricalInstrumentCaptureSchedule
{
    private static readonly TimeSpan IndiaOffset = TimeSpan.FromHours(5.5);
    private static readonly TimeOnly PreOpenCapture = new(8, 45);

    // Retain the startup sync, then refresh the official master once before
    // every subsequent session. A failed attempt is retried by the worker;
    // no fabricated pre-open snapshot is created after the open.
    public static DateTimeOffset NextPreOpenUtc(DateTimeOffset nowUtc)
    {
        var local = nowUtc.ToUniversalTime().ToOffset(IndiaOffset);
        var date = DateOnly.FromDateTime(local.DateTime);
        var candidate = new DateTimeOffset(date.ToDateTime(PreOpenCapture),
            IndiaOffset).ToUniversalTime();
        if (candidate <= nowUtc.ToUniversalTime())
            candidate = candidate.AddDays(1);
        while (candidate.ToOffset(IndiaOffset).DayOfWeek is
               DayOfWeek.Saturday or DayOfWeek.Sunday)
            candidate = candidate.AddDays(1);
        return candidate;
    }
}
