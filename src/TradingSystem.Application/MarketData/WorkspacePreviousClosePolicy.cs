namespace TradingSystem.Application.MarketData;

public static class WorkspacePreviousClosePolicy
{
    // Use the last completed regular-session candle, never an arbitrary earlier
    // intraday price. Run before trimming the chart's visible history window.
    public static WorkspacePreviousClose? Read(IEnumerable<WorkspaceCandle> candles,
        DateOnly today, TimeOnly sessionEnd)
    {
        var previous = candles.Where(c => c.IsClosed && c.IntervalSeconds > 0 && c.Close > 0 &&
                DateOnly.FromDateTime(c.OpenTimeUtc.ToOffset(TimeSpan.FromHours(5.5)).DateTime) < today &&
                TimeOnly.FromDateTime(c.OpenTimeUtc.ToOffset(TimeSpan.FromHours(5.5)).DateTime) < sessionEnd)
            .OrderByDescending(c => c.OpenTimeUtc).FirstOrDefault();
        if (previous is null) return null;
        var local = previous.OpenTimeUtc.ToOffset(TimeSpan.FromHours(5.5));
        var end = new DateTimeOffset(local.Date.Add(sessionEnd.ToTimeSpan()), local.Offset);
        return previous.OpenTimeUtc < end && previous.OpenTimeUtc.AddSeconds(previous.IntervalSeconds) == end
            ? new(previous.Close, DateOnly.FromDateTime(local.DateTime)) : null;
    }
}
