using TradingSystem.Application.Execution;

namespace TradingSystem.UnitTests;

public sealed class HistoricalSessionRegimeClassifierTests
{
    [Fact]
    public void GroupsCompleteDirectionalAndAlternatingDaysWithoutPolicyLookahead()
    {
        var date = new DateOnly(2026, 10, 29);
        var start = new DateTimeOffset(date.ToDateTime(new TimeOnly(9, 15)),
            TimeSpan.FromHours(5.5)).ToUniversalTime();
        var up = Enumerable.Range(0, 375).Select(minute => new HistoricalReplayBar(
            start.AddMinutes(minute), 60, 22000m + minute,
            22002m + minute, 21998m + minute, 22001m + minute)
        { Symbol = "NIFTY" }).ToArray();
        var range = Enumerable.Range(0, 375).Select(minute => new HistoricalReplayBar(
            start.AddMinutes(minute), 60, minute % 2 == 0 ? 22000m : 22010m,
            22012m, 21998m, minute % 2 == 0 ? 22010m : 22000m)
        { Symbol = "NIFTY" }).ToArray();
        var full = HistoricalSessionCoverageEvaluator.Evaluate(date,
            up.Select(value => value.OpenTimeUtc));

        Assert.Equal("DirectionalUp", HistoricalSessionRegimeClassifier.Classify(
            Session(up), full));
        Assert.Equal("Range", HistoricalSessionRegimeClassifier.Classify(
            Session(range), full));
        Assert.Equal("Unknown", HistoricalSessionRegimeClassifier.Classify(
            Session(up[..374]), full with { MissingMinutes = 1 }));

        HistoricalReplaySessionData Session(IReadOnlyList<HistoricalReplayBar> bars) =>
            new(date, "NIFTY", bars, [], start, start.AddMinutes(375));
    }
}
