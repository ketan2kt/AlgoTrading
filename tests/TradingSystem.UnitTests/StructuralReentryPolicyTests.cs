using TradingSystem.Application.Execution;
using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class StructuralReentryPolicyTests
{
    [Theory]
    [InlineData(Direction.Buy)]
    [InlineData(Direction.Sell)]
    public void FirstEntryIsNotRestricted(Direction direction)
    {
        Assert.True(StructuralReentryPolicy.Evaluate([], direction, null, true).Permitted);
    }

    [Fact]
    public void PersistentThesisCannotReuseStructureAtTodaysRapidReentryTiming()
    {
        // Timing regression from 23 October; synthetic prices, not a historical P&L replay.
        var exit = new DateTimeOffset(2026, 10, 23, 12, 0, 14, TimeSpan.FromHours(5.5));
        var bars = Bars([110m, 109m, 108m, 107m, 106m, 105m, 104m, 103m, 102m, 101m]);
        bars = bars.Select((bar, i) => bar with { OpenTimeUtc = exit.AddSeconds(-14).AddMinutes(i - 8) }).ToArray();
        // At 12:02:01 only the 12:01 bar has formed wholly after the exit.
        var decision = StructuralReentryPolicy.Evaluate(bars, Direction.Sell, exit, true);
        Assert.False(decision.Permitted);
        Assert.Equal(1, decision.CompletedBarsSinceExit);
    }

    [Fact]
    public void PersistentBearishContinuationRemainsEligibleAfterFreshBase()
    {
        var bars = Bars([110m, 109m, 108m, 107m, 106m, 105m, 104m, 103m,
            102.8m, 102.9m, 101.5m]);
        bars[^1] = bars[^1] with { Open = 102.5m };
        Assert.True(StructuralReentryPolicy.Evaluate(bars, Direction.Sell,
            bars[7].OpenTimeUtc, true).Permitted);
    }

    [Fact]
    public void ElapsedTimeWithoutNewStructureDoesNotPermitReentry()
    {
        var bars = Bars(Enumerable.Repeat(100m, 15).ToArray());
        Assert.False(StructuralReentryPolicy.Evaluate(bars, Direction.Sell,
            bars[7].OpenTimeUtc, true).Permitted);
    }

    [Fact]
    public void RejectsImmediateSameDirectionReentryWithoutNewCandles()
    {
        var bars = Bars([100m, 101m, 102m, 103m, 104m, 105m, 106m, 107m, 108m, 109m]);
        var exit = bars[^1].OpenTimeUtc.AddSeconds(-30);

        var result = StructuralReentryPolicy.Evaluate(bars, Direction.Buy, exit);

        Assert.False(result.Permitted);
        Assert.False(result.NewStructureObserved);
    }

    [Fact]
    public void AllowsReentryAfterCompletedPullbackAndBullishRejection()
    {
        var bars = Bars([100m, 101m, 102m, 103m, 104m, 105m, 106m, 107m, 108m,
            106m, 105m, 104m, 105m, 107m]);
        var exit = bars[8].OpenTimeUtc;

        var result = StructuralReentryPolicy.Evaluate(bars, Direction.Buy, exit);

        Assert.True(result.Permitted);
        Assert.True(result.NewStructureObserved);
    }

    [Fact]
    public void AllowsPersistentTrendReentryAfterCompactContinuationBase()
    {
        var bars = Bars([100m, 101m, 102m, 103m, 104m, 105m, 106m, 107m,
            107.2m, 107.1m, 108.5m]);
        var exit = bars[7].OpenTimeUtc;

        var result = StructuralReentryPolicy.Evaluate(bars, Direction.Buy, exit, true);

        Assert.True(result.Permitted);
        Assert.Contains(result.Reasons, value => value.Contains("continuation base"));
    }

    private static StrategyPriceBar[] Bars(decimal[] closes)
    {
        var start = new DateTimeOffset(2026, 9, 23, 4, 0, 0, TimeSpan.Zero);
        return closes.Select((close, i) => new StrategyPriceBar(start.AddMinutes(i),
            i == closes.Length - 1 ? close - 1 : close, close + 1, close - 1, close)).ToArray();
    }
}
