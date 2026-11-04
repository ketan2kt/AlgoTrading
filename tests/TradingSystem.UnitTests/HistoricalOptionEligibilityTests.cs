using TradingSystem.Application.Execution;

namespace TradingSystem.UnitTests;

public sealed class HistoricalOptionEligibilityTests
{
    [Fact]
    public void RequiresPositivePreDecisionQuoteNoOlderThanAllowedAge()
    {
        var decision = new DateTimeOffset(2026, 10, 29, 4, 0, 0,
            TimeSpan.Zero);
        static HistoricalReplayFrame Frame(DateTimeOffset decision,
            DateTimeOffset quoteAt, decimal price) => new(decision, [],
            new Dictionary<string, HistoricalReplayQuote>
            {
                ["CE"] = new("CE", quoteAt, price)
            });
        var age = TimeSpan.FromSeconds(90);

        Assert.True(HistoricalOptionEligibility.HasFreshAsOfPrice(
            Frame(decision, decision.AddSeconds(-90), 100m), "CE", age));
        Assert.False(HistoricalOptionEligibility.HasFreshAsOfPrice(
            Frame(decision, decision, 100m), "CE", age));
        Assert.False(HistoricalOptionEligibility.HasFreshAsOfPrice(
            Frame(decision, decision.AddSeconds(-91), 100m), "CE", age));
        Assert.False(HistoricalOptionEligibility.HasFreshAsOfPrice(
            Frame(decision, decision.AddSeconds(-1), 0m), "CE", age));
        Assert.False(HistoricalOptionEligibility.HasFreshAsOfPrice(
            Frame(decision, decision.AddSeconds(-1), 100m), "PE", age));
    }
}
