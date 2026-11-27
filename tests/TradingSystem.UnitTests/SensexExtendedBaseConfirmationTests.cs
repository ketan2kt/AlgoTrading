using TradingSystem.Application.Execution;
using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class SensexExtendedBaseConfirmationTests
{
    [Theory]
    [InlineData(Direction.Buy)]
    [InlineData(Direction.Sell)]
    public void ExtendedBaseNeedsDirectionalImpulse(Direction side)
    {
        var bars = Bars(side, .20m);
        Assert.NotNull(SensexMaturePullbackConfirmationPolicy.ExtendedBaseRejection(
            bars, side, Price(side, 98.60m)));
    }

    [Theory]
    [InlineData(Direction.Buy)]
    [InlineData(Direction.Sell)]
    public void StrongExtendedContinuationRemainsEligible(Direction side)
    {
        Assert.Null(SensexMaturePullbackConfirmationPolicy.ExtendedBaseRejection(
            Bars(side, 1m), side, Price(side, 98.60m)));
    }

    [Theory]
    [InlineData(Direction.Buy)]
    [InlineData(Direction.Sell)]
    public void OrdinaryPullbackAndExactExtensionBoundaryAreUnchanged(Direction side)
    {
        var bars = Bars(side, .20m);
        Assert.Null(SensexMaturePullbackConfirmationPolicy.ExtendedBaseRejection(
            bars, side, Price(side, 99.60m)));
        Assert.Null(SensexMaturePullbackConfirmationPolicy.ExtendedBaseRejection(
            bars, side, Price(side, 99.10m)));
    }

    [Theory]
    [InlineData(Direction.Buy)]
    [InlineData(Direction.Sell)]
    public void ExactExistingImpulseBoundaryIsAccepted(Direction side)
    {
        Assert.Null(SensexMaturePullbackConfirmationPolicy.ExtendedBaseRejection(
            Bars(side, .90m), side, Price(side, 98.60m)));
    }

    [Theory]
    [InlineData(Direction.Buy)]
    [InlineData(Direction.Sell)]
    public void RejectionDoesNotFallThroughToPrimaryMomentumOnSameCandle(Direction side)
    {
        var bars = Bars(side, .20m);
        var state = new MarketStateAssessmentV2(
            side == Direction.Buy ? IntradayMarketStateV2.BullTrend : IntradayMarketStateV2.BearTrend,
            side, .78m, .58m, .25m, .05m, .10m, .55m, 3.1m, []);
        var fast = Price(side, 98.60m);
        var slow = Price(side, 97.60m);
        var vwap = Price(side, 97m);
        // The same existing detector must first select a compact-base entry.
        var before = DirectionalParticipationPolicy.Evaluate(bars, state, vwap, fast, slow);
        Assert.Equal(side, before.Direction);
        Assert.Contains(before.Reasons, reason => reason.Contains("continuation base"));
        var selection = SensexEntrySelectionPolicy.Evaluate(
            new(side, .8m, "Index momentum breakout", []), bars,
            SensexSetupLifecyclePolicy.Evaluate(bars), null, state, vwap, fast, slow, false);
        Assert.Equal(DirectionalParticipationPolicy.SensexStrategy, selection.Candidate.Strategy);
        Assert.Null(selection.Candidate.Direction);
        Assert.Contains(selection.Candidate.Reasons, reason => reason.Contains("lacks a directional breakout impulse"));
    }

    private static decimal Price(Direction side, decimal price) => side == Direction.Buy ? price : 200m - price;

    private static StrategyPriceBar[] Bars(Direction side, decimal body)
    {
        var start = new DateTimeOffset(2026, 1, 1, 4, 0, 0, TimeSpan.Zero);
        var bars = Enumerable.Range(0, 15).Select(i => new StrategyPriceBar(
            start.AddMinutes(i), 100m, 101m, 99m, 100m)).ToArray();
        bars[^1] = bars[^1] with { Open = 101.60m - body, High = 102m, Low = 100m, Close = 101.60m };
        return side == Direction.Buy ? bars : bars.Select(b => b with
        {
            Open = 200m - b.Open, High = 200m - b.Low, Low = 200m - b.High, Close = 200m - b.Close
        }).ToArray();
    }
}
