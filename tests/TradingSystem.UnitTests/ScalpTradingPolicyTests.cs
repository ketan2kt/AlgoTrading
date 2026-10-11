using TradingSystem.Application.Execution;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class ScalpTradingPolicyTests
{
    [Fact]
    public void SelectsFastBearishMomentumWithoutWaitingForChampionConfirmation()
    {
        var start = new DateTimeOffset(2026, 10, 12, 3, 45, 0, TimeSpan.Zero);
        var bars = Enumerable.Range(0, 18).Select(index =>
        {
            var close = 25000m - index * 8m;
            return new ScalpPriceBar(start.AddMinutes(index), close + 4m, close + 7m,
                close - 5m, close, 1000 + index * 10);
        }).ToArray();

        var result = ScalpTradingPolicy.Evaluate(bars);

        Assert.Equal(Direction.Sell, result.Direction);
        Assert.True(result.Confidence >= .60m);
        Assert.Contains(result.Evidence, value => value.Contains("EMA 5 is below"));
    }

    [Fact]
    public void RejectsAnAlreadyExtendedMoveInsteadOfChasingIt()
    {
        var start = new DateTimeOffset(2026, 10, 12, 3, 45, 0, TimeSpan.Zero);
        var bars = Enumerable.Range(0, 17).Select(index =>
            new ScalpPriceBar(start.AddMinutes(index), 100m + index, 102m + index,
                99m + index, 101m + index, 1000)).ToList();
        bars.Add(new(start.AddMinutes(17), 117m, 145m, 116m, 142m, 3000));

        var result = ScalpTradingPolicy.Evaluate(bars);

        Assert.Null(result.Direction);
        Assert.Equal("Extended", result.Regime);
    }

    [Fact]
    public void RunnerStopLocksNetProfitAndNeverMovesBackward()
    {
        Assert.Equal(105m, ScalpTradingPolicy.TrailingStop(100m, 95m, 107m, 4m, 2m, 101m));
        Assert.Equal(105m, ScalpTradingPolicy.TrailingStop(100m, 105m, 106m, 4m, 2m, 101m));
        Assert.Equal(95m, ScalpTradingPolicy.TrailingStop(100m, 95m, 103m, 4m, 2m, 101m));
    }
}
