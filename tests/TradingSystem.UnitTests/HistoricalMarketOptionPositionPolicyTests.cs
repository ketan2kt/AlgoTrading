using TradingSystem.Application.Execution;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class HistoricalMarketOptionPositionPolicyTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 4, 4, 0, 0, TimeSpan.Zero);
    private const string Symbol = "BANKNIFTY55000CE";
    private static readonly NiftyOptionContractCandidate Contract = new(Guid.Empty,
        Symbol, InstrumentType.CallOption, new DateOnly(2026, 11, 24), 55000m, 30, .05m);

    [Fact]
    public void RecordedBankTrailUsesLaterBidsAndLeavesExistingStopAuthoritative()
    {
        var policy = Policy();
        var position = Position();
        HistoricalReplayPortfolioFrame At(int seconds, decimal bid, decimal spot, decimal stop = 90m)
        {
            var at = position.EntryAtUtc.AddSeconds(seconds);
            var open = position with { StopPrice = stop };
            return new(new(at, [], new Dictionary<string, HistoricalReplayQuote>
            {
                [Symbol] = new(Symbol, at, bid) { BidPrice = bid, OfferPrice = bid + .05m,
                    BidQuantity = 150, OfferQuantity = 150, SourceAtUtc = at, DepthSourceAtUtc = at },
                ["BANKNIFTY"] = new("BANKNIFTY", at, spot) { SourceAtUtc = at }
            }), open, []) { OpenPositions = [open] };
        }
        Assert.Empty(policy.EvaluateExits(At(0, 100m, 55000m)));
        Assert.Empty(policy.EvaluateExits(At(2, 103m, 55010m)));
        Assert.Empty(policy.EvaluateExits(At(4, 104m, 55010m)));
        Assert.Empty(policy.EvaluateExits(At(6, 99m, 54990m)));
        Assert.Equal(BankNiftyConfirmedTrail.ExitReason, Assert.Single(policy.EvaluateExits(At(10, 99m, 54980m))).Reason);
        Assert.Equal("StopLossHit", Assert.Single(policy.EvaluateExits(At(12, 89m, 54970m))).Reason);
    }

    [Fact]
    public void AsymmetricTargetsDoNotChangeTrailingOrPartialMilestones()
    {
        var policy = Policy(partial: true);
        var now = Start.AddMinutes(3);
        HistoricalReplayPortfolioFrame AtTarget(decimal target)
        {
            var position = Position() with { InitialTargetPrice = target, TargetPrice = target, StopPrice = 94m };
            return new(new(now, [], new Dictionary<string, HistoricalReplayQuote>
                { [Symbol] = Quote(3, 107.50m, 107.45m) }), position, [])
                { OpenPositions = [position] };
        }

        var close = AtTarget(115m);
        var distant = AtTarget(150m);
        Assert.Equal(Assert.Single(policy.EvaluateStopUpdates(close)), Assert.Single(policy.EvaluateStopUpdates(distant)));
        Assert.Equal(60, Assert.Single(policy.EvaluatePartialExits(close)).Quantity);
        Assert.Equal(60, Assert.Single(policy.EvaluatePartialExits(distant)).Quantity);
        Assert.Empty(policy.EvaluateExits(close));
        Assert.Empty(policy.EvaluateExits(distant));
    }

    [Theory]
    [InlineData("sensex", "SENSEX", "Index momentum breakout")]
    [InlineData("sensex", "SENSEX", "Sensex controlled early pullback")]
    [InlineData("bank-nifty", "BANKNIFTY", "Bank index directional breakout")]
    public void StrongWinnerRunsWithFreshEvidenceButStopAndTimeExitRemainAuthoritative(
        string market, string underlying, string strategy)
    {
        var now = Start.AddMinutes(21);
        var symbol = underlying + "55000CE";
        var contract = Contract with { TradingSymbol = symbol };
        var position = Position() with { Symbol = symbol, Strategy = strategy,
            EntryAtUtc = Start.AddMinutes(18), LatestPrice = 115m, StopPrice = 105m };
        var bars = Enumerable.Range(0, 21).Select(i => new HistoricalReplayBar(
            Start.AddMinutes(i), 60, 55000m + i * 5m, 55006m + i * 5m,
            54999m + i * 5m, 55005m + i * 5m) { Symbol = underlying }).ToArray();
        var quotes = new Dictionary<string, HistoricalReplayQuote>
        { [symbol] = new(symbol, now, 115m), [underlying] = new(underlying, now, 55105m) };
        var frame = new HistoricalReplayPortfolioFrame(new(now, bars, quotes), position, [])
            { OpenPositions = [position] };
        var settings = new HistoricalMarketOptionExitSettings(new TimeOnly(15, 15),
            true, true, true, .55m, 3, true, .8m, 5, 5, 60);
        var policy = new HistoricalMarketOptionPositionPolicy(new Entry(), market, underlying, settings, [contract]);
        Assert.True(policy.ShouldExtendTarget(frame, position));
        Assert.Empty(policy.EvaluateExits(frame));
        Assert.NotEmpty(policy.EvaluatePartialExits(frame));
        var stopped = position with { StopPrice = 115m };
        Assert.Equal("StopLossHit", Assert.Single(policy.EvaluateExits(frame with
            { OpenPosition = stopped, OpenPositions = [stopped] })).Reason);
        var stale = new Dictionary<string, HistoricalReplayQuote>(quotes)
            { [underlying] = new(underlying, now.AddSeconds(-6), 55105m) };
        Assert.False(policy.ShouldExtendTarget(frame with { Market = new(now, bars, stale) }, position));
        Assert.Equal("TargetHit", Assert.Single(policy.EvaluateExits(
            frame with { Market = new(now, bars, stale) })).Reason);
        var timed = new HistoricalMarketOptionPositionPolicy(new Entry(), market, underlying,
            settings with { ForcedExit = TimeOnly.FromDateTime(now.ToOffset(TimeSpan.FromHours(5.5)).DateTime) }, [contract]);
        Assert.NotEmpty(timed.EvaluateExits(frame));
        Assert.All(policy.EvaluateStopUpdates(frame), update => Assert.True(update.NewStopPrice >= position.StopPrice));
    }

    [Fact]
    public void LaterBidFillsWholeLotPartialAndProtectsRemainingDirectionalWinner()
    {
        var policy = Policy(partial: true);
        var winnerBars = Enumerable.Range(0, 27).Select(index =>
            new HistoricalReplayBar(Start.AddMinutes(index - 20), 60, 55000m + index * 5m,
                55006m + index * 5m, 54999m + index * 5m, 55005m + index * 5m)
                { Symbol = "BANKNIFTY" }).ToArray();
        var result = HistoricalPortfolioReplay.Run(winnerBars, [
            new("BANKNIFTY", Start.AddMinutes(3).AddSeconds(-1), 55115m),
            new("BANKNIFTY", Start.AddMinutes(4).AddSeconds(-1), 55120m),
            new("BANKNIFTY", Start.AddMinutes(5).AddSeconds(-1), 55125m),
            new("BANKNIFTY", Start.AddMinutes(6).AddSeconds(-1), 55130m),
            // At 0.8R the new partial is eligible, but the runner target is not hit.
            Quote(2, 100m, 99.5m), Quote(3, 108m, 107.5m),
            Quote(4, 116m, 115.5m), Quote(5, 107m, 106.5m),
            Quote(6, 106m, 105.5m)
        ], policy, new(.10m, .20m, 0m, 0m, 30, 0), Start, Start.AddMinutes(8));
        var partial = Assert.Single(result.Trades, value => value.IsPartialExit);
        Assert.Equal(60, partial.Quantity);
        Assert.Equal(Start.AddMinutes(4), partial.ExitAtUtc);
        Assert.Equal(115.5m, partial.ExitPrice);
        var remainder = Assert.Single(result.Trades, value => !value.IsPartialExit);
        Assert.Equal(90, remainder.Quantity);
        Assert.Equal(Start.AddMinutes(6), remainder.ExitAtUtc);
        Assert.Equal(105.5m, remainder.ExitPrice);
        Assert.Equal(0, result.MissingExitQuoteCount);
        Assert.True(result.Trades.All(value => value.Costs > 0m));
    }

    [Fact]
    public void UpdatedPaperStopUsesLtpNotAnEarlierBidBelowThatStop()
    {
        var result = HistoricalPortfolioReplay.Run(Bars(), [
            Quote(2, 100m, 99.5m), Quote(3, 106m, 99m),
            Quote(4, 107m, 100m), Quote(5, 99m, 98.5m),
            Quote(6, 98m, 97.5m)
        ], Policy(), new(.10m, .20m, 0m, 0m, 30, 0), Start, Start.AddMinutes(8));
        var trade = Assert.Single(result.Trades);
        Assert.Equal(Start.AddMinutes(6), trade.ExitAtUtc);
        Assert.Equal(97.5m, trade.ExitPrice);
    }

    [Fact]
    public void StaleOptionEvidenceCannotChangeStopOrExit()
    {
        var policy = Policy();
        var position = Position();
        var frame = new HistoricalReplayPortfolioFrame(new(Start.AddMinutes(5), [],
            new Dictionary<string, HistoricalReplayQuote>
            { [Symbol] = Quote(3, 130m, 129m) }), position, [])
            { OpenPositions = [position] };
        Assert.Empty(policy.EvaluateStopUpdates(frame));
        Assert.Empty(policy.EvaluateExits(frame));
        Assert.Empty(policy.EvaluatePartialExits(frame));
    }

    [Fact]
    public void SpecialtyPositionsAreNotManagedByNormalSessionWrapper()
    {
        var position = Position() with { Strategy = "Expansion Pair|example" };
        var frame = new HistoricalReplayPortfolioFrame(new(Start.AddMinutes(3), [],
            new Dictionary<string, HistoricalReplayQuote> { [Symbol] = Quote(3, 80m, 79m) }),
            position, []) { OpenPositions = [position] };
        Assert.Empty(Policy().EvaluateStopUpdates(frame));
        Assert.Empty(Policy().EvaluateExits(frame));
    }

    private static HistoricalReplayOpenPosition Position() => new(Symbol,
        "Bank index directional breakout", Start.AddMinutes(2), 100m, 100m, 150, 90m, 110m)
    { OriginalQuantity = 150, InitialStopPrice = 90m, InitialTargetPrice = 110m,
        LotSize = 30, UnderlyingDirection = Direction.Buy };

    private static HistoricalMarketOptionPositionPolicy Policy(bool partial = false) =>
        new(new Entry(), "bank-nifty", "BANKNIFTY", new(new TimeOnly(15, 15),
            partial, true, false, .55m, 3, true, .8m, 90, 5, 60), [Contract]);

    private static HistoricalReplayBar[] Bars() => Enumerable.Range(0, 21).Select(index =>
        new HistoricalReplayBar(Start.AddMinutes(index - 20), 60, 55000m + index * 5m,
            55006m + index * 5m, 54999m + index * 5m, 55005m + index * 5m)
            { Symbol = "BANKNIFTY" }).ToArray();
    private static HistoricalReplayQuote Quote(int minute, decimal last, decimal bid) =>
        new(Symbol, Start.AddMinutes(minute), last) { BidPrice = bid, OfferPrice = last };

    private sealed class Entry : IHistoricalReplayPortfolioPolicy
    {
        public string CodeVersion => "synthetic-bank-entry-v1";
        public IReadOnlyList<HistoricalReplayCandidate> Evaluate(HistoricalReplayPortfolioFrame frame) =>
            frame.Market.AvailableAtUtc == Start.AddMinutes(1)
                ? [new(Symbol, frame.Market.AvailableAtUtc, "Bank index directional breakout", .75m, 150)
                    { LotSize = 30, UnderlyingDirection = Direction.Buy, PlannedStopPrice = 90m,
                        PlannedTargetPrice = 110m, TriggerOnLastPrice = true }]
                : [];
    }
}
