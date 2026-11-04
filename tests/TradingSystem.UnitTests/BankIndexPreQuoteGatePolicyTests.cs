using TradingSystem.Application.Execution;
using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class BankIndexPreQuoteGatePolicyTests
{
    [Fact]
    public void LocationRejectionPreservesProductionShortCircuitAndReason()
    {
        var session = Bars([106m, 107m, 108m, 109.8m]);
        var previous = Bars([104m, 106m, 108m, 110m]);
        var original = MarketLocationPolicy.Evaluate(session, previous,
            Direction.Buy, 1m, 108m, 105m);

        var shared = BankIndexPreQuoteGatePolicy.Evaluate(session, previous,
            Direction.Buy, 1m, 108m, 105m);

        Assert.False(shared.Permitted);
        Assert.Equal("MarketLocationRejected", shared.RejectionOutcome);
        Assert.Equal(original.Permitted, shared.Location.Permitted);
        Assert.Equal(original.Context, shared.Location.Context);
        Assert.Equal(original.NearestSupport, shared.Location.NearestSupport);
        Assert.Equal(original.NearestResistance, shared.Location.NearestResistance);
        Assert.Equal(original.Reasons, shared.Location.Reasons);
        Assert.Equal(original.Reasons, shared.Reasons);
        Assert.Null(shared.Move);
        Assert.Null(shared.MatureEdge);
    }

    [Fact]
    public void AcceptedLocationUsesUnchangedMoveAndMaturePolicies()
    {
        var session = Bars([103m, 102m, 101m]).ToList();
        session.Add(new(new DateTimeOffset(2026, 10, 29, 4, 15, 0,
            TimeSpan.Zero), 100.5m, 101m, 100m, 100.8m));
        var previous = Bars([100m, 105m, 110m]);
        var move = SessionMoveQualityPolicy.Evaluate(session, Direction.Buy, 1m);
        var mature = MatureEdgeEntryPolicy.Evaluate(move,
            MultiTimeframeDecisionPolicy.Analyze(session, Direction.Buy,
                104m, 100m), Direction.Buy);

        var shared = BankIndexPreQuoteGatePolicy.Evaluate(session, previous,
            Direction.Buy, 1m, 104m, 100m);

        Assert.True(shared.Location.Permitted);
        Assert.Equal(move.Permitted, shared.Move?.Permitted);
        Assert.Equal(move.PeakMoveAtr, shared.Move?.PeakMoveAtr);
        Assert.Equal(move.Reasons, shared.Move?.Reasons);
        Assert.Equal(mature.Permitted, shared.MatureEdge?.Permitted);
        Assert.Equal(mature.Reasons, shared.MatureEdge?.Reasons);
        Assert.Equal(move.Permitted && mature.Permitted, shared.Permitted);
        Assert.Equal([.. move.Reasons, .. mature.Reasons], shared.Reasons);
    }

    private static StrategyPriceBar[] Bars(decimal[] closes) => closes
        .Select((close, index) => new StrategyPriceBar(
            new DateTimeOffset(2026, 10, 29, 4, 0, 0, TimeSpan.Zero)
                .AddMinutes(index * 5), close, close + .2m,
            close - .2m, close)).ToArray();
}
