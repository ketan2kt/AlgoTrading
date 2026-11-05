using TradingSystem.Application.Execution;
using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class SensexMaturePullbackConfirmationTests
{
    [Fact]
    public void RecordedNovember5EntryWaitsForStructureBreak()
    {
        var bars = November5Bars();
        var result = SensexMaturePullbackConfirmationPolicy.Evaluate(bars, Direction.Buy, 8.890549m);
        Assert.True(result.Applies);
        Assert.False(result.Permitted);
        Assert.Equal(79464.35m, result.ConfirmationLevel);
        Assert.InRange(result.RoomAtr!.Value, .151m, .152m);
    }

    [Fact]
    public void SelectedParticipationCannotFallBackAfterConfirmationRejection()
    {
        var bars = November5Bars();
        var state = new MarketStateAssessmentV2(IntradayMarketStateV2.BullTrend,
            Direction.Buy, .791372m, .546777m, .357143m, .034483m, .9146m,
            1.995149m, 8.890549m, []);
        var original = DirectionalParticipationPolicy.Evaluate(bars, state,
            79262.304345m, 79389.759843m, 79317.274637m);
        Assert.Equal(Direction.Buy, original.Direction);
        var selected = SensexEntrySelectionPolicy.Evaluate(
            new(null, .65m, "Index momentum breakout", []), bars,
            new(SensexSetupPhase.Emerging, Direction.Buy, 11, -.83015m, 2.190788m, []),
            null, state, 79262.304345m, 79389.759843m, 79317.274637m, false);
        Assert.Equal(DirectionalParticipationPolicy.SensexStrategy, selected.Candidate.Strategy);
        Assert.Null(selected.Candidate.Direction);
        Assert.Contains(selected.Candidate.Reasons, reason => reason.Contains("Waiting for structure confirmation"));
    }

    [Theory]
    [InlineData(Direction.Buy)]
    [InlineData(Direction.Sell)]
    public void NarrowGatePreservesImmatureMovesAndRequiresCloseNotWick(Direction direction)
    {
        var bars = November5Bars();
        if (direction == Direction.Sell)
            bars = bars.Select(bar => bar with { Open = 160000m - bar.Open,
                High = 160000m - bar.Low, Low = 160000m - bar.High,
                Close = 160000m - bar.Close }).ToArray();
        var immature = SensexMaturePullbackConfirmationPolicy.Evaluate(bars, direction, 4.25m);
        Assert.True(immature.Permitted);
        Assert.False(immature.Applies);
        bars[^1] = direction == Direction.Buy
            ? bars[^1] with { High = 79500m }
            : bars[^1] with { Low = 80500m };
        Assert.False(SensexMaturePullbackConfirmationPolicy.Evaluate(bars, direction, 8.89m).Permitted);
    }

    [Theory]
    [InlineData(Direction.Buy)]
    [InlineData(Direction.Sell)]
    public void ConfirmedBreakRemainsEligibleEvenWithNearbyOlderLevel(Direction direction)
    {
        var bars = November5Bars();
        // Synthetic boundary control: an older level is near the new close,
        // but all four bars of the actual pullback have been reclaimed.
        bars[0] = bars[0] with { High = 79470m };
        bars[^1] = bars[^1] with { High = 79466m, Close = 79465m };
        if (direction == Direction.Sell)
            bars = bars.Select(bar => bar with { Open = 160000m - bar.Open,
                High = 160000m - bar.Low, Low = 160000m - bar.High,
                Close = 160000m - bar.Close }).ToArray();
        var result = SensexMaturePullbackConfirmationPolicy.Evaluate(bars, direction, 8.89m);
        Assert.True(result.Applies);
        Assert.True(result.Permitted);
        bars[^1] = bars[^1] with { Close = result.ConfirmationLevel!.Value };
        Assert.False(SensexMaturePullbackConfirmationPolicy.Evaluate(bars, direction, 8.89m).Permitted);
    }

    [Fact]
    public void NoNearbyOpposingLevelDoesNotBlockMatureContinuation()
    {
        var bars = November5Bars();
        bars[^1] = bars[^1] with { High = 79500m, Close = 79500m };
        var result = SensexMaturePullbackConfirmationPolicy.Evaluate(bars, Direction.Buy, 8.89m);
        Assert.True(result.Permitted);
        Assert.False(result.Applies);
        Assert.Null(result.RoomAtr);
    }

    [Fact]
    public void RecordedNovember4WinnerIsAlsoFilteredAndMustNotBeHidden()
    {
        // This actual entry later netted +6712.94. The gate is not a win predictor.
        var bars = Bars(new(2026, 11, 4, 7, 46, 0, TimeSpan.Zero), new decimal[,]
        {
            {77037.02m,77053.69m,77037.02m,77043.79m},
            {77043.02m,77052.50m,77032.97m,77036.55m},
            {77040.04m,77052.66m,77030.69m,77032.53m},
            {77040.29m,77055.13m,77040.29m,77053.85m},
            {77057.95m,77062.37m,77051.27m,77053.88m},
            {77054.25m,77060.84m,77054.25m,77054.86m},
            {77057.69m,77057.78m,77045.59m,77047.81m},
            {77050.35m,77061.28m,77050.35m,77056.31m},
            {77058.35m,77060.08m,77035.74m,77045.31m},
            {77040.22m,77087.09m,77039.69m,77087.09m},
            {77088.81m,77105.12m,77077.30m,77081.67m},
            {77079.48m,77084.00m,77072.41m,77074.99m},
            {77078.63m,77097.09m,77078.63m,77097.09m},
            {77094.42m,77096.91m,77086.50m,77087.79m},
            {77086.57m,77090.74m,77082.91m,77090.73m}
        });
        Assert.False(SensexMaturePullbackConfirmationPolicy.Evaluate(bars, Direction.Buy, 7.178311m).Permitted);
    }

    // Recorded one-minute SENSEX OHLC from authenticated workspace history,
    // 5 Nov 13:37–13:51 IST. No candle after the decision's close is used.
    private static StrategyPriceBar[] November5Bars() => Bars(
        new(2026, 11, 5, 8, 7, 0, TimeSpan.Zero), new decimal[,]
        {
            {79173.88m,79218.20m,79172.85m,79218.20m},
            {79215.41m,79289.39m,79211.45m,79286.60m},
            {79283.41m,79300.44m,79275.24m,79287.91m},
            {79291.29m,79303.59m,79289.41m,79301.60m},
            {79298.34m,79322.89m,79298.34m,79320.24m},
            {79324.29m,79360.90m,79324.29m,79359.17m},
            {79360.66m,79370.41m,79324.46m,79325.43m},
            {79324.99m,79338.48m,79315.06m,79336.36m},
            {79334.71m,79334.71m,79320.08m,79330.23m},
            {79325.10m,79365.81m,79318.50m,79357.07m},
            {79359.43m,79415.52m,79359.37m,79407.83m},
            {79419.91m,79464.35m,79419.91m,79424.29m},
            {79418.38m,79443.75m,79409.14m,79441.51m},
            {79430.66m,79439.69m,79420.14m,79420.14m},
            {79423.97m,79435.29m,79414.60m,79434.19m}
        });

    private static StrategyPriceBar[] Bars(DateTimeOffset start, decimal[,] values) =>
        Enumerable.Range(0, values.GetLength(0)).Select(index => new StrategyPriceBar(
            start.AddMinutes(index), values[index, 0], values[index, 1],
            values[index, 2], values[index, 3])).ToArray();
}
