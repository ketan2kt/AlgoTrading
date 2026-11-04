namespace TradingSystem.Application.Execution;

public sealed record HistoricalIndexMinuteCoverage(int ExpectedMinutes,
    int PresentMinutes, int MissingMinutes, int LongestMissingRunMinutes)
{
    public bool IsComplete => MissingMinutes == 0;
}

public static class HistoricalSessionCoverageEvaluator
{
    public static HistoricalIndexMinuteCoverage Evaluate(DateOnly date,
        IEnumerable<DateTimeOffset> minuteOpenTimesUtc)
    {
        ArgumentNullException.ThrowIfNull(minuteOpenTimesUtc);
        var first = new DateTimeOffset(date.ToDateTime(new TimeOnly(9, 15)),
            TimeSpan.FromHours(5.5)).ToUniversalTime();
        const int expected = 375;
        var observed = minuteOpenTimesUtc.Select(value => value.ToUniversalTime())
            .ToHashSet();
        var present = 0;
        var run = 0;
        var longest = 0;
        for (var minute = 0; minute < expected; minute++)
        {
            if (observed.Contains(first.AddMinutes(minute)))
            {
                present++;
                run = 0;
            }
            else
            {
                run++;
                longest = Math.Max(longest, run);
            }
        }
        return new(expected, present, expected - present, longest);
    }
}
