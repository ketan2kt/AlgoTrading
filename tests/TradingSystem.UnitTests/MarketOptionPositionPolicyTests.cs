using TradingSystem.Application.Execution;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class MarketOptionPositionPolicyTests
{
    [Theory]
    [InlineData("Index momentum breakout", true)]
    [InlineData("Sensex controlled early pullback", true)]
    [InlineData("Sensex persistent directional thesis", true)]
    [InlineData("Sensex directional pullback participation", true)]
    [InlineData("Bank index directional breakout", true)]
    [InlineData("Sensex range-edge rejection", false)]
    [InlineData("unknown strategy", false)]
    public void RunnerEligibilityIncludesNamedTrendEntriesButNotRangeOrUnknown(string strategy, bool allowed)
        => Assert.Equal(allowed, MarketOptionPositionPolicy.AllowsTargetExtension(strategy));

    [Theory]
    [InlineData(Direction.Buy)]
    [InlineData(Direction.Sell)]
    public void ContinuationRequiresFreshAlignedCompletedEvidence(Direction direction)
    {
        var start = new DateTimeOffset(2026, 11, 5, 4, 0, 0, TimeSpan.Zero);
        var sign = direction == Direction.Buy ? 1m : -1m;
        var bars = Enumerable.Range(0, 21).Select(i =>
        {
            var open = 55000m + sign * i * 5m;
            var close = open + sign * 5m;
            return new TradingSystem.Application.Strategies.StrategyPriceBar(start.AddMinutes(i),
                open, Math.Max(open, close) + 1m, Math.Min(open, close) - 1m, close);
        }).ToArray();
        var now = start.AddMinutes(21);
        Assert.True(MarketOptionPositionPolicy.FreshContinuation(bars, direction, now, now, 60, 5));
        Assert.False(MarketOptionPositionPolicy.FreshContinuation(bars, direction, now, null, 60, 5));
        Assert.False(MarketOptionPositionPolicy.FreshContinuation(bars, direction, now, now.AddSeconds(-6), 60, 5));
        Assert.False(MarketOptionPositionPolicy.FreshContinuation(bars, direction, now, now.AddSeconds(1), 60, 5));
        Assert.False(MarketOptionPositionPolicy.FreshContinuation(bars, direction, now.AddMinutes(3), now.AddMinutes(3), 60, 5));
        Assert.False(MarketOptionPositionPolicy.FreshContinuation(bars,
            direction == Direction.Buy ? Direction.Sell : Direction.Buy, now, now, 60, 5));
        // Future/forming candles cannot create a confirmation before enough bars have closed.
        Assert.False(MarketOptionPositionPolicy.FreshContinuation(bars, direction,
            start.AddMinutes(8), start.AddMinutes(8), 60, 5));
    }

    [Fact]
    public void SharedExitAndWholeLotPartialOrderingMatchesOriginalProductionBranches()
    {
        var plan = ProfessionalExitPolicy.For(ProfessionalTradeRegime.Directional);
        foreach (var direction in new[] { Direction.Buy, Direction.Sell })
        foreach (var price in new[] { 85m, 100m, 115m, 125m })
        for (var flags = 0; flags < 128; flags++)
        {
            var manual = (flags & 1) != 0;
            var reversal = (flags & 2) != 0;
            var failed = (flags & 4) != 0;
            var continuation = (flags & 8) != 0;
            var forced = (flags & 16) != 0;
            var enabled = (flags & 32) != 0;
            var priorPartial = (flags & 64) != 0;
            var stop = direction == Direction.Buy ? 90m : 110m;
            var target = direction == Direction.Buy ? 120m : 80m;
            var stopHit = direction == Direction.Buy ? price <= stop : price >= stop;
            var targetHit = direction == Direction.Buy ? price >= target : price <= target;
            var close = manual || stopHit || reversal || failed || targetHit && !continuation || forced;
            var expected = !close ? null : manual ? "ManualExit" : stopHit ? "StopLossHit" :
                reversal ? "UnderlyingTrendInvalidated" : failed ? "FailedSetupExit" :
                targetHit ? "TargetHit" : "TimeExit";
            var reached = direction == Direction.Buy ? price >= 112.5m : price <= 87.5m;
            var partial = !failed && !reversal && !manual && !stopHit && !forced &&
                (!targetHit || continuation) && enabled && !priorPartial && reached ? 60 : 0;
            var actual = MarketOptionPositionPolicy.Decide(direction, price, 100m, stop,
                target, 10m, 150, 30, plan, manual, reversal, failed, continuation,
                forced, enabled, priorPartial);
            Assert.Equal(expected, actual.ExitReason);
            Assert.Equal(partial, actual.PartialQuantity);
        }
    }

    [Fact]
    public void ExtractedProtectionMatchesOriginalCostAndTrailingFormula()
    {
        var plan = ProfessionalExitPolicy.For(ProfessionalTradeRegime.Directional);
        foreach (var peak in new[] { 100m, 104m, 108m, 120m, 150m })
        {
            var original = PaperProtectiveStopPolicy.Calculate(Direction.Buy, 100m,
                90m, peak, plan.BreakEvenTriggerR, plan.TrailDistanceR,
                plan.ProfitLockTriggerR, plan.ProfitLockR);
            if (peak - 100m >= 10m * plan.BreakEvenTriggerR)
            {
                var costs = PaperTradingCostModel.CalculateOptionCharges([
                    new(PaperOptionTransactionSide.Buy, 100m, 150),
                    new(PaperOptionTransactionSide.Sell, Math.Max(.05m, original), 150)]).Total;
                original = PaperProtectiveStopPolicy.ProtectNetProfit(Direction.Buy, 100m,
                    original, 150, costs);
            }
            Assert.Equal(Math.Max(95m, original), MarketOptionPositionPolicy.ProtectedStop(
                Direction.Buy, 100m, 10m, peak, 95m, 150, plan));
        }
    }
}
