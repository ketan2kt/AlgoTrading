using TradingSystem.Application.Execution;
using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

// Recorded 2026-11-05 SENSEX spot candles from the authenticated workspace API,
// aggregated only from complete groups of five one-minute bars. Decision times
// below are recorded candle OPEN times; replay may use data through their close.
// This verifies the rejected gates, not executable option fills or net returns.
public sealed class SensexNovember5MorningGateReplayTests
{
    [Theory]
    [InlineData(9, 30, Direction.Sell)]
    [InlineData(9, 33, Direction.Buy)]
    [InlineData(9, 41, Direction.Sell)]
    [InlineData(10, 7, Direction.Buy)]
    [InlineData(10, 9, Direction.Buy)]
    [InlineData(10, 17, Direction.Sell)]
    [InlineData(10, 24, Direction.Sell)]
    [InlineData(10, 27, Direction.Sell)]
    [InlineData(10, 30, Direction.Buy)]
    public void RecordedChopRejectionsLackedCompletedDirectionalConfirmation(
        int hour, int minute, Direction direction)
    {
        var asOf = new DateTimeOffset(2026, 11, 5, hour, minute, 0,
            TimeSpan.FromHours(5.5)).AddMinutes(1);
        var available = Bars().Where(bar => bar.OpenTimeUtc.AddMinutes(5) <= asOf).ToArray();
        Assert.True(available.Length >= 3);
        var result = MiddayChopConfirmationPolicy.ConfirmCompletedStructure(available, direction, 300);
        Assert.True(result.Applies);
        Assert.False(result.Permitted);
    }

    [Fact]
    public void SameRecordedMorningCanConfirmAnAlignedMoveWithoutBlanketVeto()
    {
        var asOf = new DateTimeOffset(2026, 11, 5, 9, 40, 0, TimeSpan.FromHours(5.5));
        var available = Bars().Where(bar => bar.OpenTimeUtc.AddMinutes(5) <= asOf).ToArray();
        Assert.True(MiddayChopConfirmationPolicy.ConfirmCompletedStructure(available, Direction.Buy, 300).Permitted);
        Assert.False(MiddayChopConfirmationPolicy.ConfirmCompletedStructure(available, Direction.Sell, 300).Permitted);
        // Passing this one gate is not permission to bypass lifecycle, option,
        // market-location, freshness, spread or portfolio-risk admission.
    }

    [Theory]
    [InlineData(SensexSetupPhase.Extended, Direction.Sell, 2, 1.0502359243622947, .531166436985557)] // 09:31
    [InlineData(SensexSetupPhase.Emerging, Direction.Buy, 4, .05736788888297589, .23203663912337275)] // 09:40
    [InlineData(SensexSetupPhase.Extended, Direction.Buy, 8, .8865942461501731, 1.1433406164779842)] // 09:45
    [InlineData(SensexSetupPhase.Emerging, Direction.Buy, 7, .47108276561820556, 1.392562588709104)] // 09:46
    [InlineData(SensexSetupPhase.Pullback, Direction.Buy, 0, -5.096732218187924, .9039178348178888)] // 10:02
    public void RecordedLifecycleInputsReproduceTimingRejections(SensexSetupPhase phase,
        Direction direction, int age, double extension, double separation)
    {
        Assert.False(SensexSetupLifecyclePolicy.AllowsChampionEntry(new(phase,
            direction, age, (decimal)extension, (decimal)separation, [])));
    }

    private static StrategyPriceBar[] Bars()
    {
        decimal[][] values = [
            [78650.70m,78755.81m,78645.16m,78722.85m],
            [78722.76m,78741.11m,78659.18m,78685.60m],
            [78678.98m,78703.46m,78639.41m,78690.96m],
            [78685.89m,78685.89m,78545.22m,78608.42m],
            [78627.74m,78770.76m,78617.68m,78736.22m],
            [78736.09m,78819.38m,78736.09m,78809.71m],
            [78809.31m,78914.29m,78807.26m,78905.80m],
            [78906.82m,78994.29m,78894.03m,78982.05m],
            [78981.29m,78999.90m,78880.83m,78882.42m],
            [78874.63m,78883.83m,78813.82m,78833.64m],
            [78837.88m,78843.76m,78733.91m,78763.19m],
            [78765.17m,78775.93m,78702.85m,78734.19m],
            [78754.75m,78858.36m,78754.75m,78843.81m],
            [78839.21m,78841.82m,78785.35m,78827.42m],
            [78825.21m,78832.44m,78778.08m,78789.83m],
            [78792.32m,78845.59m,78733.13m,78735.98m]
        ];
        var start = new DateTimeOffset(2026, 11, 5, 3, 45, 0, TimeSpan.Zero);
        return values.Select((v, i) => new StrategyPriceBar(start.AddMinutes(i * 5),
            v[0], v[1], v[2], v[3])).ToArray();
    }
}
