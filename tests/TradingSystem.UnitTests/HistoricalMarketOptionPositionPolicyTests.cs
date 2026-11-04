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
    public void LaterBidFillsWholeLotPartialAndProtectsRemainingDirectionalWinner()
    {
        var policy = Policy(partial: true);
        var result = HistoricalPortfolioReplay.Run(Bars(), [
            Quote(2, 100m, 99.5m), Quote(3, 115m, 114.5m),
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
