using TradingSystem.Application.Execution;
using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class MiddayChopConfirmationPolicyTests
{
    private static readonly MarketStructureQualitySnapshot NoisyStructure = new(
        MarketStructureQualityState.NoisyChop, Direction.Buy, .25m, .75m, .30m,
        .80m, null, .30m, false, ["chop"]);

    [Theory]
    [InlineData(10, 59)]
    [InlineData(13, 31)]
    public void DoesNotAddTimeGateOutsideMidday(int hour, int minute)
    {
        var result = MiddayChopConfirmationPolicy.Evaluate(new(hour, minute), [],
            Direction.Buy, NoisyStructure, 60);

        Assert.False(result.Applies);
        Assert.True(result.Permitted);
    }

    [Fact]
    public void MiddayChopWaitsWhenFiveMinuteStructureIsNotConfirmed()
    {
        var bars = MinutesFromFiveMinuteCloses([100m, 101m, 100m], [101m, 102m, 101m],
            [102m, 101m, 100.5m]);

        var result = MiddayChopConfirmationPolicy.Evaluate(new(12, 0), bars,
            Direction.Buy, NoisyStructure, 60);

        Assert.True(result.Applies);
        Assert.False(result.Permitted);
        Assert.Contains("five-minute", result.Reasons[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MiddayChopAllowsConfirmedFiveMinuteContinuation()
    {
        var bars = MinutesFromFiveMinuteCloses([100m, 100.2m, 100.4m], [100.5m, 100.8m, 101m],
            [101m, 101.5m, 102m]);

        var result = MiddayChopConfirmationPolicy.Evaluate(new(12, 0), bars,
            Direction.Buy, NoisyStructure, 60);

        Assert.True(result.Applies);
        Assert.True(result.Permitted);
    }

    [Fact]
    public void MiddayCleanTrendRemainsEligibleWithoutExtraConfirmation()
    {
        var structure = NoisyStructure with
        {
            State = MarketStructureQualityState.DevelopingTrend,
            ChopScore = .30m
        };

        var result = MiddayChopConfirmationPolicy.Evaluate(new(12, 0), [],
            Direction.Buy, structure, 60);

        Assert.False(result.Applies);
        Assert.True(result.Permitted);
    }

    private static List<StrategyPriceBar> MinutesFromFiveMinuteCloses(
        decimal[] first, decimal[] second, decimal[] third)
    {
        var buckets = new[] { first, second, third };
        var bars = new List<StrategyPriceBar>();
        var start = new DateTimeOffset(2026, 10, 27, 5, 30, 0, TimeSpan.Zero);
        for (var bucket = 0; bucket < buckets.Length; bucket++)
        {
            var levels = buckets[bucket];
            var bucketStart = levels[0];
            var direction = Math.Sign(levels[^1] - bucketStart);
            for (var minute = 0; minute < 5; minute++)
            {
                var open = minute == 0 ? bucketStart : levels[(minute - 1) % levels.Length];
                var close = minute == 4 ? levels[^1] : levels[minute % levels.Length];
                var high = Math.Max(open, close) + .10m;
                var low = Math.Min(open, close) - .10m;
                // Ensure the final bucket closes above the previous structure high.
                if (bucket == 2 && minute == 4 && direction > 0) close = levels[^1] + 2m;
                bars.Add(new(start.AddMinutes(bucket * 5 + minute), open,
                    Math.Max(high, close), Math.Min(low, close), close));
            }
        }
        return bars;
    }
}
