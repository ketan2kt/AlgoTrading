namespace TradingSystem.Application.Execution;

// Ex-post descriptive grouping for comparing results, never a trading input.
// A missing/partial session is Unknown rather than guessed from a few bars.
public static class HistoricalSessionRegimeClassifier
{
    public static string Classify(HistoricalReplaySessionData session,
        HistoricalIndexMinuteCoverage coverage)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(coverage);
        if (!coverage.IsComplete) return "Unknown";
        var bars = session.Bars.Where(value => value.Symbol == session.IndexSymbol &&
                value.IntervalSeconds == 60 && value.OpenTimeUtc >= session.StartUtc &&
                value.OpenTimeUtc < session.EndUtc)
            .OrderBy(value => value.OpenTimeUtc).ToArray();
        if (bars.Length != coverage.ExpectedMinutes) return "Unknown";
        var displacement = bars[^1].Close - bars[0].Open;
        var path = Math.Abs(bars[0].Close - bars[0].Open);
        for (var index = 1; index < bars.Length; index++)
            path += Math.Abs(bars[index].Close - bars[index - 1].Close);
        if (path <= 0m) return "Range";
        var efficiency = Math.Abs(displacement) / path;
        return efficiency >= 0.60m
            ? displacement > 0m ? "DirectionalUp" : "DirectionalDown"
            : efficiency <= 0.25m ? "Range" : "Mixed";
    }
}
