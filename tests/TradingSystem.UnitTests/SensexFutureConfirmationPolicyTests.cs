using TradingSystem.Application.Execution;
using TradingSystem.Application.Strategies;

namespace TradingSystem.UnitTests;

public sealed class SensexFutureConfirmationPolicyTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 29, 3, 45, 0,
        TimeSpan.Zero);

    [Fact]
    public void UsesOnlyAlignedIndexBarsAndPriorPositiveFutureVolume()
    {
        var result = SensexFutureConfirmationPolicy.Evaluate([
            Bar(0, 100m), Bar(5, 110m), Bar(10, 120m)
        ], [new(At, 100), new(At.AddMinutes(5), 200)]);

        Assert.Equal(106.66666666666666666666666667m, result.Vwap);
        Assert.Equal(2m, result.RelativeVolume);
        Assert.True(result.Ready);
    }

    [Fact]
    public void MissingBaselineOrFutureDataFailsConfirmation()
    {
        var index = new[] { Bar(0, 100m) };
        Assert.False(SensexFutureConfirmationPolicy.Evaluate(index, []).Ready);
        Assert.False(SensexFutureConfirmationPolicy.Evaluate(index,
            [new(At, 100)]).Ready);
    }

    [Fact]
    public void FutureBarAfterDecisionCandleIsRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            SensexFutureConfirmationPolicy.Evaluate([Bar(0, 100m)],
                [new(At.AddMinutes(5), 100)]));
    }

    [Fact]
    public void ReorderedInputsCannotProduceLookAheadConfirmation()
    {
        Assert.Throws<ArgumentException>(() =>
            SensexFutureConfirmationPolicy.Evaluate(
                [Bar(5, 110m), Bar(0, 100m)],
                [new(At, 100)]));
    }

    [Fact]
    public void ExtractedCalculationMatchesTheFormerProductionFormula()
    {
        var random = new Random(72931);
        for (var example = 0; example < 100; example++)
        {
            var index = Enumerable.Range(0, 35).Select(minute =>
                Bar(minute, 70000m + random.Next(-500, 500))).ToArray();
            var future = Enumerable.Range(0, 35).Where(_ => random.Next(0, 5) != 0)
                .Select(minute => new SensexFutureVolumeBar(At.AddMinutes(minute),
                    random.Next(0, 4) == 0 ? 0 : random.Next(1, 5000)))
                .ToArray();
            var actual = SensexFutureConfirmationPolicy.Evaluate(index, future);
            var volumeByTime = future.Where(value => value.Volume > 0)
                .ToDictionary(value => value.OpenTimeUtc, value => value.Volume);
            var aligned = index.Where(value =>
                volumeByTime.ContainsKey(value.OpenTimeUtc)).ToArray();
            var totalVolume = aligned.Sum(value => volumeByTime[value.OpenTimeUtc]);
            var latestFuture = future.LastOrDefault(value => value.Volume > 0);
            var baseline = future.Where(value => value.Volume > 0)
                .TakeLast(Math.Min(20, future.Length)).SkipLast(1).ToArray();
            var averageVolume = baseline.Length == 0 ? 0m :
                baseline.Average(value => (decimal)value.Volume);
            var priorRelative = latestFuture is null || averageVolume <= 0m
                ? 0m : latestFuture.Volume / averageVolume;
            var priorVwap = totalVolume <= 0m ? 0m : aligned.Sum(value =>
                ((value.High + value.Low + value.Close) / 3m) *
                volumeByTime[value.OpenTimeUtc]) / totalVolume;
            Assert.Equal(priorVwap, actual.Vwap);
            Assert.Equal(priorRelative, actual.RelativeVolume);
        }
    }

    private static StrategyPriceBar Bar(int minutes, decimal close) =>
        new(At.AddMinutes(minutes), close, close, close, close);
}
