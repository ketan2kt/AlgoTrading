using TradingSystem.Application.Execution;
using TradingSystem.Application.Strategies;

namespace TradingSystem.UnitTests;

public sealed class NiftySignalFeaturePolicyTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 4,
        3, 45, 0, TimeSpan.Zero);

    [Fact]
    public void CalculatesAlignedVolumeWeightedFeaturesWithoutFutureCandles()
    {
        var spot = new[]
        {
            Bar(0, 100m, 102m, 98m, 101m),
            Bar(1, 101m, 104m, 100m, 103m),
            Bar(2, 103m, 106m, 102m, 105m)
        };
        var futures = new[]
        {
            Bar(0, 100m, 102m, 98m, 101m) with { Volume = 100m },
            Bar(1, 101m, 104m, 100m, 103m) with { Volume = 200m },
            Bar(2, 103m, 106m, 102m, 105m) with { Volume = 300m }
        };

        var result = Assert.IsType<NiftySignalFeatures>(
            NiftySignalFeaturePolicy.Calculate(spot, futures,
                new TimeOnly(9, 20)));

        Assert.Equal(2m, result.RelativeFuturesVolume);
        Assert.Equal(103m, result.Vwap);
        Assert.Equal(102m, result.OpeningRangeHigh);
        Assert.Equal(98m, result.OpeningRangeLow);
        Assert.Equal(102.12m, result.FastEma);
        Assert.InRange(result.SlowEma, 101.52m, 101.54m);
        Assert.InRange(result.AtrPercent, 3.80m, 3.82m);
    }

    [Fact]
    public void MissingAlignedVolumeFailsClosed()
    {
        var spot = new[]
        {
            Bar(0, 100m, 102m, 98m, 101m),
            Bar(1, 101m, 104m, 100m, 103m)
        };
        var future = new[]
        {
            Bar(2, 100m, 102m, 98m, 101m) with { Volume = 100m },
            Bar(3, 101m, 104m, 100m, 103m) with { Volume = 200m }
        };

        Assert.Null(NiftySignalFeaturePolicy.Calculate(spot, future,
            new TimeOnly(9, 20)));
    }

    private static StrategyPriceBar Bar(int index, decimal open,
        decimal high, decimal low, decimal close) =>
        new(Start.AddMinutes(index * 5), open, high, low, close);
}
