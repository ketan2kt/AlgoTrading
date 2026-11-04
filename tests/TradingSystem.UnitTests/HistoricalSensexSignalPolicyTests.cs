using TradingSystem.Application.Broker;
using TradingSystem.Application.Execution;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class HistoricalSensexSignalPolicyTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 4, 4, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Date = new(2026, 11, 4);
    private static readonly NiftyOptionContractCandidate Contract = new(Guid.Empty,
        "SENSEX73000CE", InstrumentType.CallOption, Date.AddDays(8), 73000m, 20, .05m);

    [Fact]
    public void DeferredAdmissionUsesSharedSensexPlanAndALaterTwoSidedQuote()
    {
        var policy = Policy();
        var context = Context();
        var pending = new HistoricalReplayCandidate(Contract.TradingSymbol, Start,
            "Sensex directional pullback participation", .8m, 100)
        { RequiresDeferredQuoteAdmission = true, DeferredSensexProfessionalContext = context };
        var quote = new HistoricalReplayQuote(Contract.TradingSymbol, Start.AddSeconds(2), 100m)
            { BidPrice = 99.5m, OfferPrice = 100m, Volume = 1000, OpenInterest = 5000m };
        var frame = new HistoricalReplayPortfolioFrame(new(quote.AvailableAtUtc, [],
            new Dictionary<string, HistoricalReplayQuote>()), null, []);
        Assert.Null(policy.AssessDeferredEntry(frame, pending, quote with { AvailableAtUtc = Start }));
        Assert.Null(policy.AssessDeferredEntry(frame, pending, quote with { BidPrice = null }));
        var admitted = Assert.IsType<HistoricalReplayCandidate>(policy.AssessDeferredEntry(frame, pending, quote));
        var original = SensexOptionAdmissionPolicy.Evaluate(new(new GrowwQuote(100m, null,
            99.5m, null, 100m, null, 1000m, 5000m, null), 10m, .05m, 20, context));
        Assert.Equal(original.Stop, admitted.PlannedStopPrice);
        Assert.Equal(original.Target, admitted.PlannedTargetPrice);
        Assert.Equal(100, admitted.Quantity);
        Assert.False(admitted.RequiresDeferredQuoteAdmission);
    }

    [Fact]
    public void RealSensexSetupCanPassDiscoveryAdmissionAndLaterModeledExit()
    {
        var bars = Session();
        var signalAt = Start.AddMinutes(31);
        var policy = Policy();
        var frame = new HistoricalReplayPortfolioFrame(new(signalAt, bars,
            new Dictionary<string, HistoricalReplayQuote>()), null, []);
        var decision = policy.EvaluateDetailed(frame);
        Assert.True(decision.Candidates.Count == 1, decision.Outcome);
        var candidate = Assert.Single(decision.Candidates);
        Assert.Equal(Direction.Buy, candidate.UnderlyingDirection);
        Assert.Equal(Contract.TradingSymbol, candidate.Symbol);
        var positionPolicy = new HistoricalMarketOptionPositionPolicy(policy, "sensex", "SENSEX",
            new(new TimeOnly(15, 15), true, false, false, .55m, 3, true, .8m, 90, 5, 60), [Contract]);
        HistoricalReplayQuote Quote(int second, decimal price) =>
            new(Contract.TradingSymbol, signalAt.AddSeconds(second), price)
                { BidPrice = price - .5m, OfferPrice = price, Volume = 1000, OpenInterest = 5000m };
        var result = HistoricalPortfolioReplay.Run(bars,
            [Quote(2, 100m), Quote(4, 100m), Quote(8, 120m), Quote(10, 119m)],
            positionPolicy, new(.1m, .1m, 0m, 0m, 30, 0), signalAt,
            signalAt.AddMinutes(1), exchangeCode: "BSE");
        var fill = Assert.Single(result.Trades);
        Assert.Equal(signalAt.AddSeconds(4), fill.EntryAtUtc);
        Assert.Equal(signalAt.AddSeconds(10), fill.ExitAtUtc);
        Assert.Equal(118.5m, fill.ExitPrice);
        Assert.Equal("BSE", fill.ExchangeCode);
        Assert.True(fill.Costs > 0m);
        Assert.Equal(0, result.MissingExitQuoteCount);
    }

    [Fact]
    public void FutureFuturesEvidenceCannotMakeAnEarlierFrameEligible()
    {
        var now = Start.AddMinutes(20);
        var bars = Session().Where(value => value.Symbol == "SENSEX" ||
            value.AvailableAtUtc > now).ToArray();
        var frame = new HistoricalReplayPortfolioFrame(new(now, bars,
            new Dictionary<string, HistoricalReplayQuote>()), null, []);
        Assert.Empty(Policy().Evaluate(frame));
    }

    private static HistoricalSensexSignalPolicy Policy() => new(Guid.NewGuid(), Date,
        "SENSEX-FUT", new(new TimeOnly(9, 15), new TimeOnly(15, 0), new TimeOnly(15, 15),
            5, 60, 10m, 90), [Contract]);
    private static ProfessionalTradeAssessmentInput Context() => new("sensex",
        ProfessionalTradeRegime.Directional, .8m, 1m, 2m, null, null, null,
        8, null, null, 0m, false, 0, true);
    private static HistoricalReplayBar[] Session()
    {
        var spot = Enumerable.Range(0, 27).Select(index =>
        {
            var open = 73000m + index * .35m;
            return new HistoricalReplayBar(Start.AddMinutes(index), 60,
                open, open + .395m, open - .15m, open + .245m) { Symbol = "SENSEX" };
        }).Concat(new[]
        {
            new HistoricalReplayBar(Start.AddMinutes(27), 60, 73009.30m, 73009.45m, 73007.50m, 73007.70m),
            new HistoricalReplayBar(Start.AddMinutes(28), 60, 73007.70m, 73008.10m, 73007.50m, 73007.95m),
            new HistoricalReplayBar(Start.AddMinutes(29), 60, 73007.95m, 73008.10m, 73007.55m, 73007.80m),
            new HistoricalReplayBar(Start.AddMinutes(30), 60, 73007.80m, 73008.80m, 73007.65m, 73008.65m)
        }.Select(value => value with { Symbol = "SENSEX" })).ToArray();
        return spot.Concat(spot.Select(value => value with { Symbol = "SENSEX-FUT", Volume = 100 }))
            .Append(new HistoricalReplayBar(Start.AddDays(-1), 60, 72900m, 72950m, 72880m, 72920m)
                { Symbol = "SENSEX" }).ToArray();
    }
}
