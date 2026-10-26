using TradingSystem.Application.Execution;
using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class SessionMoveQualityPolicyTests
{
    [Theory]
    [InlineData(Direction.Buy)]
    [InlineData(Direction.Sell)]
    public void DoesNotVetoWhenSessionMoveIsNotMature(Direction direction)
    {
        var bars = Bars([
            (100m, 101m, 99m, 100m),
            (100m, 103m, 99m, 102m),
            (102m, 104m, 101m, 103m),
            (103m, 104m, 102m, 103m)]);

        Assert.True(SessionMoveQualityPolicy.Evaluate(bars, direction, 10m).Permitted);
    }

    [Theory]
    [InlineData(Direction.Buy)]
    [InlineData(Direction.Sell)]
    public void RejectsChasingMatureSessionExtremeWithoutFreshRetest(Direction direction)
    {
        var bars = direction == Direction.Buy
            ? Bars([(100m, 101m, 99m, 100m), (131.8m, 132m, 131.7m, 131.9m),
                (132.2m, 133m, 132m, 132.8m), (133.2m, 134m, 133m, 133.8m),
                (134.2m, 135m, 134m, 134.8m)])
            : Bars([(100m, 101m, 99m, 100m), (68.2m, 68.3m, 68m, 68.1m),
                (67.8m, 68m, 67m, 67.2m), (66.8m, 67m, 66m, 66.2m),
                (65.8m, 66m, 65m, 65.2m)]);

        var decision = SessionMoveQualityPolicy.Evaluate(bars, direction, 10m);

        Assert.False(decision.Permitted);
        Assert.True(decision.PeakMoveAtr >= SessionMoveQualityPolicy.MatureMoveAtr);
    }

    [Theory]
    [InlineData(Direction.Buy)]
    [InlineData(Direction.Sell)]
    public void AllowsMatureMoveAfterCompletedPullbackAndResumption(Direction direction)
    {
        var bars = direction == Direction.Buy
            ? Bars([(100m, 101m, 99m, 100m), (129m, 132m, 128m, 131m),
                (131m, 132m, 131m, 131.5m), (132m, 134m, 132m, 133.8m),
                (134m, 135m, 133.8m, 134.8m)])
            : Bars([(100m, 101m, 99m, 100m), (68m, 72m, 68m, 69m),
                (69m, 69m, 68m, 68.5m), (68m, 68m, 66m, 66.2m),
                (66m, 66.2m, 65m, 65.2m)]);

        var decision = SessionMoveQualityPolicy.Evaluate(bars, direction, 10m);

        Assert.True(decision.Permitted, string.Join(" ", decision.Reasons));
        Assert.Contains(decision.Reasons, reason => reason.Contains("pullback"));
    }

    private static StrategyPriceBar[] Bars((decimal Open, decimal High, decimal Low, decimal Close)[] values)
    {
        var start = new DateTimeOffset(2026, 10, 26, 3, 45, 0, TimeSpan.Zero);
        return values.Select((value, index) => new StrategyPriceBar(start.AddMinutes(index),
            value.Open, value.High, value.Low, value.Close)).ToArray();
    }
}
