using TradingSystem.Application.Execution;
using TradingSystem.Application.Regime;
using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class HistoricalNiftyEndToEndTests
{
    private static readonly DateOnly Date = new(2026, 10, 29);
    private static readonly DateTimeOffset Start = new(2026, 10, 29,
        3, 45, 0, TimeSpan.Zero);

    [Fact]
    public void SharedNiftyDecisionCanOpenAndExitFromOneFrozenSession()
    {
        var closes = Enumerable.Range(0, 15)
            .Select(index => 22000m + index * .30m).ToArray();
        closes[12] = 22003.0m;
        closes[13] = 22003.4m;
        closes[14] = 22005.0m;
        var bars = Enumerable.Range(0, 15).SelectMany(index =>
        {
            var close = closes[index];
            var open = index == 0 ? 22000m : closes[index - 1];
            return new[]
            {
                new HistoricalReplayBar(Start.AddMinutes(index * 5),
                    300, open, Math.Max(open, close) + .8m,
                    Math.Min(open, close) - .8m, close)
                { Symbol = "NIFTY" },
                new HistoricalReplayBar(Start.AddMinutes(index * 5),
                    300, open + 10m, Math.Max(open, close) + 10.8m,
                    Math.Min(open, close) + 9.2m, close + 10m)
                { Symbol = "NIFTY-FUT", Volume = 100 }
            };
        }).Concat(Enumerable.Range(0, 15).Select(index =>
            new HistoricalReplayBar(Start.AddDays(-1).AddMinutes(index * 5),
                300, 21995m, 21997m, 21993m, 21995m)
            { Symbol = "NIFTY" })).ToArray();
        var signalAt = Start.AddMinutes(75);
        var quotes = new[]
        {
            new HistoricalReplayQuote("NIFTY", signalAt.AddSeconds(-10),
                22005m),
            OptionQuote(signalAt.AddSeconds(5), 100m),
            OptionQuote(signalAt.AddSeconds(10), 100m),
            OptionQuote(signalAt.AddSeconds(20), 120m),
            OptionQuote(signalAt.AddSeconds(25), 119m)
        };
        var strategy = new MomentumExpansionStrategy(new());
        var admission = new NiftyOptionAdmissionSettings(
            false, 1.5m, 500m, 100m, 100m, 10m, 1m,
            8, 100_000m, 1_000_000m, 100_000m,
            new TimeOnly(9, 15), new TimeOnly(15, 0),
            new TimeOnly(15, 15), 20, 30,
            true, .35m, 1m, .6m);
        var entry = new HistoricalNiftySignalPolicy(Guid.NewGuid(),
            "NIFTY", "NIFTY-FUT", Date, 21995m,
            new TimeOnly(9, 30), 15, 15, 300, 1,
            [new(Guid.NewGuid(), "NIFTY22000CE",
                InstrumentType.CallOption,
                Date.AddDays(5), 22000m, 75, .05m)],
            new NiftyOptionSelectionOptions(),
            new MarketRegimeEngine(new MarketRegimeOptions()),
            strategy, admission);
        var policy = new HistoricalNiftyPositionPolicy(entry,
            new(new TimeOnly(15, 15), false, false, false,
                false, .6m, false, .55m, 3, false, .8m,
                30, 20, 300));

        var result = HistoricalPortfolioReplay.Run(bars, quotes, policy,
            new(.10m, .10m, 0m, 0m, 120, 0),
            Start, Start.AddMinutes(90));

        Assert.True(result.CandidateCount > 0);
        var fill = Assert.Single(result.Trades);
        Assert.Equal(signalAt, fill.DecisionAtUtc);
        Assert.Equal(signalAt.AddSeconds(10), fill.EntryAtUtc);
        Assert.Equal(signalAt.AddSeconds(25), fill.ExitAtUtc);
        Assert.Equal("ObservedTarget", fill.ExitReason);
        Assert.Equal(0, result.MissingEntryQuoteCount);
        Assert.Equal(0, result.MissingExitQuoteCount);

        var sensexOpen = new HistoricalReplayOpenPosition("SENSEX73000CE",
            "sensex", signalAt.AddMinutes(-5), 200m, 205m,
            100, 180m, 240m);
        var market = new HistoricalReplayFrame(signalAt,
            bars.Where(value => value.AvailableAtUtc <= signalAt).ToArray(),
            new Dictionary<string, HistoricalReplayQuote>
            { ["NIFTY"] = quotes[0] });
        var crossMarketFrame = new HistoricalReplayPortfolioFrame(market,
            null, [])
        { PortfolioOpenPositions = [sensexOpen] };
        var frozen = Assert.Single(entry.Evaluate(crossMarketFrame))
            .DeferredNiftyAdmission;
        Assert.NotNull(frozen);
        Assert.Equal(0m, frozen.PortfolioContext.ExistingCapitalExposure);
        Assert.Equal(0, frozen.PortfolioContext.OpenPositions);
        Assert.True(frozen.ProfessionalContext.CorrelatedSameDirectionExposure);
    }

    private static HistoricalReplayQuote OptionQuote(DateTimeOffset at,
        decimal price) => new("NIFTY22000CE", at, price)
    {
        BidPrice = price - .5m,
        OfferPrice = price,
        Volume = 10_000,
        OpenInterest = 50_000m
    };

}
