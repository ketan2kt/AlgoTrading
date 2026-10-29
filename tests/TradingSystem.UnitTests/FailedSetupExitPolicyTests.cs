using TradingSystem.Application.Execution;
using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class FailedSetupExitPolicyTests
{
    private static readonly DateTimeOffset Entry =
        new(2026, 10, 29, 10, 34, 5, TimeSpan.FromMinutes(330));
    private static readonly DateTimeOffset Observed = Entry.AddMinutes(3);

    [Fact]
    public void AdverseOptionAndFailedUnderlyingResumptionExitBeforeFullStop()
    {
        var decision = FailedSetupExitPolicy.Evaluate(Direction.Buy,
            207.60m, 198.05m, 20.80m, 207.85m, Entry, Observed,
            104m, FallingBars());

        Assert.True(decision.ShouldExit);
        Assert.InRange(decision.AdverseR, .45m, .47m);
    }

    [Fact]
    public void OrdinaryPullbackOrPriorWinnerDoesNotTriggerFastExit()
    {
        var bars = FallingBars();
        Assert.False(FailedSetupExitPolicy.Evaluate(Direction.Buy,
            207.60m, 198.05m, 20.80m, 207.85m, Entry, Observed,
            111m, bars).ShouldExit);
        Assert.False(FailedSetupExitPolicy.Evaluate(Direction.Buy,
            207.60m, 198.05m, 20.80m, 213m, Entry, Observed,
            104m, bars).ShouldExit);
        Assert.False(FailedSetupExitPolicy.Evaluate(Direction.Buy,
            207.60m, 202m, 20.80m, 207.85m, Entry, Observed,
            104m, bars).ShouldExit);
    }

    [Fact]
    public void FastExitDoesNotArmImmediatelyOrAfterTheInitialWindow()
    {
        Assert.False(FailedSetupExitPolicy.IsAdverseCandidate(
            207.60m, 198.05m, 20.80m, 207.85m, Entry, Entry.AddSeconds(30)));
        Assert.False(FailedSetupExitPolicy.IsAdverseCandidate(
            207.60m, 198.05m, 20.80m, 207.85m, Entry, Entry.AddMinutes(9)));
    }

    [Fact]
    public void PutOptionUsesOppositeUnderlyingDirection()
    {
        var rising = FallingBars().Select(bar => new StrategyPriceBar(bar.OpenTimeUtc,
            220m - bar.Open, 220m - bar.Low, 220m - bar.High,
            220m - bar.Close)).ToArray();
        var decision = FailedSetupExitPolicy.Evaluate(Direction.Sell,
            207.60m, 198.05m, 20.80m, 207.85m, Entry, Observed,
            116m, rising);

        Assert.True(decision.ShouldExit);
    }

    private static StrategyPriceBar[] FallingBars()
    {
        var start = new DateTimeOffset(2026, 10, 29, 10, 27, 0,
            TimeSpan.FromMinutes(330));
        var bars = new List<StrategyPriceBar>();
        for (var index = 0; index < 8; index++)
            bars.Add(new(start.AddMinutes(index), 100m + index,
                102m + index, 99m + index, 101m + index));
        bars.Add(new(start.AddMinutes(8), 108m, 111m, 107m, 110m));
        bars.Add(new(start.AddMinutes(9), 110m, 111m, 107m, 108m));
        return bars.ToArray();
    }
}
