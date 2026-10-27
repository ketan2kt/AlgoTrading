using TradingSystem.Application.MarketData;

namespace TradingSystem.UnitTests;

public sealed class PreSessionOutcomePolicyTests
{
    private static readonly DateOnly Day = new(2026, 10, 27);
    private static readonly DateTimeOffset Start = new(2026, 10, 27, 3, 45, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = new(2026, 10, 27, 9, 55, 0, TimeSpan.Zero);

    [Fact]
    public void ScoresOnlySufficientlyCoveredDirectionalJudgements()
    {
        var morning = Morning("Up");
        var correct = PreSessionOutcomePolicy.Assess("nifty", Day, 100m, 110m, 98m, 108m,
            300, Start, End, End.AddMinutes(20), morning);
        var partial = PreSessionOutcomePolicy.Assess("nifty", Day, 100m, 110m, 98m, 108m,
            40, Start, End, End.AddMinutes(20), morning);

        Assert.Equal("Up", correct.ActualDirection);
        Assert.Equal("Correct", correct.Assessment);
        Assert.Equal("Sufficient", correct.Coverage);
        Assert.Equal("Unscored", partial.Assessment);
    }

    [Fact]
    public void WrongDirectionalHypothesisRemainsRecordedAsIncorrect()
    {
        var result = PreSessionOutcomePolicy.Assess("sensex", Day, 100m, 102m, 88m, 90m,
            300, Start, End, End.AddMinutes(20), Morning("Up"));

        Assert.Equal("Down", result.ActualDirection);
        Assert.Equal("Incorrect", result.Assessment);
    }

    private static PreSessionMarketBrief Morning(string judgement) => new(
        "nifty", Day, "Overnight", Start.AddMinutes(-30), "Unclear", "test",
        judgement, "Low", "test", 100m, 100m, 110m, 90m, Start.AddDays(-1),
        null, null, null, null, null, null, [], [], []);
}
