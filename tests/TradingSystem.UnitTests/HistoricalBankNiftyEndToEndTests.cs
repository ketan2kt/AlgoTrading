using TradingSystem.Application.Execution;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class HistoricalBankNiftyEndToEndTests
{
    [Theory]
    [InlineData(4, 0)]
    [InlineData(8, 49)] // 14:50 IST: a confirmed late trade is still eligible.
    public void RealBankEntryAndSharedProtectionRunOnSameFrozenClock(int hourUtc, int minuteUtc)
    {
        var date = new DateOnly(2026, 11, 4);
        var start = new DateTimeOffset(2026, 11, 4, hourUtc, minuteUtc, 0, TimeSpan.Zero);
        var bars = Session(start);
        var signalAt = start.AddMinutes(31);
        const string symbol = "BANKNIFTY55000PE";
        var contracts = new[] { new NiftyOptionContractCandidate(Guid.Empty, symbol,
            InstrumentType.PutOption, new DateOnly(2026, 11, 24), 55000m, 30, .05m) };
        var entry = new HistoricalBankNiftySignalPolicy(Guid.Empty, date, 60,
            new TimeOnly(9, 15), new TimeOnly(15, 0), 5, contracts);
        var frame = new HistoricalReplayPortfolioFrame(new(signalAt, bars,
            new Dictionary<string, HistoricalReplayQuote>()), null, []);
        var candidate = Assert.Single(entry.Evaluate(frame));
        Assert.Equal(Direction.Sell, candidate.UnderlyingDirection);
        Assert.True(candidate.RequiresDeferredQuoteAdmission);
        var policy = new HistoricalMarketOptionPositionPolicy(entry, "bank-nifty", "BANKNIFTY",
            new(new TimeOnly(15, 15), true, true, false, .55m, 3, true, .8m, 90, 5, 60), contracts);
        HistoricalReplayQuote Quote(int second, decimal last) =>
            new(symbol, signalAt.AddSeconds(second), last)
                { BidPrice = last - .5m, OfferPrice = last };
        var result = HistoricalPortfolioReplay.Run(bars, [
            new("BANKNIFTY", signalAt.AddSeconds(8), 54988.5m),
            new("BANKNIFTY", signalAt.AddSeconds(10), 54988.4m),
            new("BANKNIFTY", signalAt.AddSeconds(12), 54988.3m),
            Quote(2, 100m), Quote(4, 100m), Quote(8, 120m),
            Quote(10, 119m), Quote(12, 105m), Quote(14, 104m)
        ], policy, new(.1m, .1m, 0m, 0m, 30, 0), signalAt, signalAt.AddMinutes(1));
        Assert.Equal(2, result.Trades.Count);
        var partial = Assert.Single(result.Trades, value => value.IsPartialExit);
        Assert.Equal(signalAt.AddSeconds(4), partial.EntryAtUtc);
        Assert.Equal(signalAt.AddSeconds(10), partial.ExitAtUtc);
        Assert.Equal(60, partial.Quantity);
        var remaining = Assert.Single(result.Trades, value => !value.IsPartialExit);
        Assert.Equal(signalAt.AddSeconds(14), remaining.ExitAtUtc);
        Assert.Equal(90, remaining.Quantity);
        Assert.Equal(0, result.MissingEntryQuoteCount);
        Assert.Equal(0, result.MissingExitQuoteCount);
    }

    private static HistoricalReplayBar[] Session(DateTimeOffset start) =>
        Enumerable.Range(0, 30).Select(index =>
        {
            var open = 55000m - .35m * index;
            var close = open - .245m;
            return new HistoricalReplayBar(start.AddMinutes(index), 60,
                open, open + .15m, close - .15m, close) { Symbol = "BANKNIFTY" };
        }).Append(new HistoricalReplayBar(start.AddMinutes(30), 60,
            54989.5m, 54989.6m, 54988.3m, 54988.5m) { Symbol = "BANKNIFTY" })
            .Append(new HistoricalReplayBar(start.AddDays(-1), 60,
                55100m, 55105m, 55095m, 55100m) { Symbol = "BANKNIFTY" }).ToArray();
}
