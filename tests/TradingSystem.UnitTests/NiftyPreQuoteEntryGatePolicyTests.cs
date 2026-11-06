using TradingSystem.Application.Execution;
using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class NiftyPreQuoteEntryGatePolicyTests
{
    [Fact]
    public void SharedGateKeepsProductionLocationOutcomeAndReasons()
    {
        var session = Bars([106m, 107m, 108m, 109.8m]);
        var previous = Bars([104m, 106m, 108m, 110m]);
        var location = MarketLocationPolicy.Evaluate(session, previous,
            Direction.Buy, 1m, 108m, 105m);
        var timeframes = MultiTimeframeDecisionPolicy.Analyze(session,
            Direction.Buy, 108m, 105m);

        var shared = NiftyPreQuoteEntryGatePolicy.Evaluate(new TimeOnly(10, 0),
            session, previous, Direction.Buy,
            MarketStructureQualitySnapshot.Unavailable, timeframes, 300,
            1m, 108m, 105m);

        Assert.False(location.Permitted);
        Assert.False(shared.Permitted);
        Assert.Equal("MarketLocationRejected", shared.RejectionOutcome);
        Assert.Equal(location.Context, shared.LocationContext);
        Assert.Equal(location.Reasons, shared.Reasons);
    }

    [Fact]
    public void SharedGateKeepsProductionMoveAndMaturityDecisionOrder()
    {
        var session = Bars([103m, 102m, 101m]).ToList();
        session.Add(new(new DateTimeOffset(2026, 10, 29, 4, 15, 0,
            TimeSpan.Zero), 100.5m, 101m, 100m, 100.8m));
        var previous = Bars([100m, 105m, 110m]);
        var location = MarketLocationPolicy.Evaluate(session, previous,
            Direction.Buy, 1m, 104m, 100m);
        var move = SessionMoveQualityPolicy.Evaluate(session, Direction.Buy, 1m);
        var timeframes = MultiTimeframeDecisionPolicy.Analyze(session,
            Direction.Buy, 104m, 100m);
        var mature = MatureEdgeEntryPolicy.Evaluate(move, timeframes,
            Direction.Buy, session, 300);

        var shared = NiftyPreQuoteEntryGatePolicy.Evaluate(new TimeOnly(10, 0),
            session, previous, Direction.Buy,
            MarketStructureQualitySnapshot.Unavailable, timeframes, 300,
            1m, 104m, 100m);

        Assert.True(location.Permitted);
        Assert.Equal(move.Permitted && mature.Permitted, shared.Permitted);
        Assert.Equal(mature.Reasons, shared.Reasons);
    }

    private static StrategyPriceBar[] Bars(decimal[] closes) => closes
        .Select((close, index) => new StrategyPriceBar(
            new DateTimeOffset(2026, 10, 29, 4, 0, 0, TimeSpan.Zero)
                .AddMinutes(index * 5), close, close + .2m,
            close - .2m, close)).ToArray();
}
