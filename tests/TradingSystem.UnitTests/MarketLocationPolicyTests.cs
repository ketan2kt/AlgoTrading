using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class MarketLocationPolicyTests
{
    [Fact]
    public void RejectsBuyImmediatelyBelowMajorResistance()
    {
        var result = MarketLocationPolicy.Evaluate(
            Bars([106m, 107m, 108m, 109.8m]), Bars([104m, 106m, 108m, 110m]),
            Direction.Buy, 1m, 108m, 105m);

        Assert.False(result.Permitted);
        Assert.Contains(result.Reasons, reason => reason.Contains("major resistance"));
    }

    [Fact]
    public void PermitsConfirmedBullishRejectionAtSupport()
    {
        var session = Bars([103m, 102m, 101m]).ToList();
        session.Add(new(DateTimeOffset.UtcNow, 100.5m, 101m, 100m, 100.8m));

        var result = MarketLocationPolicy.Evaluate(session, Bars([100m, 105m, 110m]),
            Direction.Buy, 1m, 104m, 100m);

        Assert.True(result.Permitted, string.Join(" | ", result.Reasons));
        Assert.StartsWith("SupportRejection", result.Context);
    }

    [Fact]
    public void RejectsDirectionlessMiddleOfSessionRange()
    {
        var result = MarketLocationPolicy.Evaluate(
            Bars([100m, 104m, 101m, 102m]), Bars([95m, 100m, 108m]),
            Direction.Buy, 1m, 104m, 100m);

        Assert.False(result.Permitted);
        Assert.Contains(result.Reasons, reason => reason.Contains("middle"));
    }

    private static StrategyPriceBar[] Bars(decimal[] closes) => closes.Select((close, index) =>
        new StrategyPriceBar(DateTimeOffset.UtcNow.AddMinutes(index * 5), close, close + 0.2m,
            close - 0.2m, close)).ToArray();
}
