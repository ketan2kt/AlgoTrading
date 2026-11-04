using TradingSystem.Application.Execution;

namespace TradingSystem.UnitTests;

public sealed class HistoricalReplayControlsTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 4, 4, 0, 0, TimeSpan.Zero);
    private static readonly HistoricalReplayParameters Parameters = new(.2m, .5m, 0m, 0m, 30, 0);

    [Fact]
    public void StateNeverSeesAFutureCommandAndManualRequestDoesNotExitALaterReentry()
    {
        var events = HistoricalReplayControls.Validate([
            Event(2, "nifty", HistoricalReplayControlKind.KillSwitch, true),
            Event(4, "nifty", HistoricalReplayControlKind.KillSwitch, false),
            Event(3, "nifty", HistoricalReplayControlKind.ManualExit, true, "NIFTY-CE")]);
        Assert.False(HistoricalReplayControls.At(events, Start.AddSeconds(1)).NiftyKillSwitch);
        Assert.True(HistoricalReplayControls.At(events, Start.AddSeconds(2)).NiftyKillSwitch);
        var state = HistoricalReplayControls.At(events, Start.AddSeconds(4));
        Assert.False(state.NiftyKillSwitch);
        var position = new HistoricalReplayOpenPosition("NIFTY-CE", "normal", Start.AddSeconds(2), 100, 100, 30, 80, 150);
        Assert.True(state.ManualExitRequested(position));
        Assert.False(state.ManualExitRequested(position with { EntryAtUtc = Start.AddSeconds(4) }));
    }

    [Theory]
    [InlineData("NIFTY", "nifty")]
    [InlineData("SENSEX", "sensex")]
    [InlineData("BANKNIFTY", "bank-nifty")]
    public void ManualExitOccursAtNextQuoteNotAtRequestOrPriorMark(string prefix, string market)
    {
        var symbol = prefix + "-CE";
        var policy = new HistoricalNormalControlPolicy(new Entry(symbol), prefix, 5);
        var result = Run(policy, [Quote(symbol, 61, 100), Quote(symbol, 63, 99)],
            [Event(62, market, HistoricalReplayControlKind.ManualExit, true, symbol)]);
        var fill = Assert.Single(result.Trades);
        Assert.Equal(Start.AddSeconds(61), fill.EntryAtUtc);
        Assert.Equal(Start.AddSeconds(63), fill.ExitAtUtc);
        Assert.Equal("ManualExit", fill.ExitReason);
        Assert.Equal(98.5m, fill.ExitPrice);
    }

    [Fact]
    public void KillCancelsAnUnfilledNiftyEntryInsteadOfFillingAfterTheGateClosed()
    {
        var result = Run(new HistoricalNormalControlPolicy(new Entry("NIFTY-CE"), "NIFTY", 5),
            [Quote("NIFTY-CE", 62, 100)], [Event(61, "nifty", HistoricalReplayControlKind.KillSwitch, true)]);
        Assert.Empty(result.Trades);
        Assert.Equal(0, result.MissingEntryQuoteCount);
    }

    [Theory]
    [InlineData("SENSEX")]
    [InlineData("BANKNIFTY")]
    public void NiftyOnlyKillGateDoesNotInventAnAdditionalMarketRestriction(string market)
    {
        var symbol = market + "-CE";
        var result = Run(new HistoricalNormalControlPolicy(new Entry(symbol), market, 5),
            [Quote(symbol, 61, 100), Quote(symbol, 62, 151), Quote(symbol, 63, 150)],
            [Event(59, "nifty", HistoricalReplayControlKind.KillSwitch, true)]);
        Assert.Single(result.Trades);
        Assert.NotEqual("EmergencyKillSwitch", result.Trades[0].ExitReason);
    }

    [Fact]
    public void KillClosesExistingNiftyAtALaterQuoteAndManualExitHasPriority()
    {
        var policy = new HistoricalNormalControlPolicy(new Entry("NIFTY-CE"), "NIFTY", 5);
        var events = new[] { Event(62, "nifty", HistoricalReplayControlKind.KillSwitch, true) };
        var quotes = new[] { Quote("NIFTY-CE", 61, 100), Quote("NIFTY-CE", 63, 99) };
        Assert.Equal("EmergencyKillSwitch", Assert.Single(Run(policy, quotes, events).Trades).ExitReason);
        var manual = events.Append(Event(62, "nifty", HistoricalReplayControlKind.ManualExit, true, "NIFTY-CE")).ToArray();
        Assert.Equal("ManualExit", Assert.Single(Run(policy, quotes, manual).Trades).ExitReason);
    }

    [Fact]
    public void StaleQuoteCannotFillAManualExitAtTheOldPrice()
    {
        var result = Run(new HistoricalNormalControlPolicy(new Entry("NIFTY-CE"), "NIFTY", 5),
            [Quote("NIFTY-CE", 61, 100), Quote("NIFTY-CE", 75, 99), Quote("NIFTY-CE", 76, 98)],
            [Event(70, "nifty", HistoricalReplayControlKind.ManualExit, true, "NIFTY-CE")]);
        Assert.Equal(Start.AddSeconds(76), Assert.Single(result.Trades).ExitAtUtc);
    }

    [Fact]
    public void ConflictingSameTimestampRiskStatesAreRejected()
    {
        Assert.Throws<ArgumentException>(() => HistoricalReplayControls.Validate([
            Event(2, "nifty", HistoricalReplayControlKind.KillSwitch, true),
            Event(2, "nifty", HistoricalReplayControlKind.KillSwitch, false)]));
        Assert.Throws<ArgumentException>(() => HistoricalReplayControls.Validate([
            Event(2, "sensex", HistoricalReplayControlKind.KillSwitch, true)]));
    }

    private static HistoricalReplayResult Run(IHistoricalReplayPortfolioPolicy policy,
        IReadOnlyList<HistoricalReplayQuote> quotes, IReadOnlyList<HistoricalReplayControlEvent> events) =>
        HistoricalPortfolioReplay.Run([new(Start, 60, 100, 101, 99, 100) { Symbol = "INDEX" }],
            quotes, policy, Parameters, Start, Start.AddMinutes(2), sourceControls: events);
    private static HistoricalReplayControlEvent Event(int second, string market, HistoricalReplayControlKind kind,
        bool enabled, string? symbol = null) => new(Guid.NewGuid(), Start.AddSeconds(second), market, kind, enabled, symbol);
    private static HistoricalReplayQuote Quote(string symbol, int second, decimal price) =>
        new(symbol, Start.AddSeconds(second), price) { BidPrice = price - .5m, OfferPrice = price };
    private sealed class Entry(string symbol) : IHistoricalReplayPortfolioPolicy
    {
        public string CodeVersion => "control-test";
        public IReadOnlyList<HistoricalReplayCandidate> Evaluate(HistoricalReplayPortfolioFrame frame) =>
            frame.OpenPositions.Count == 0 && frame.ClosedTrades.Count == 0 ?
                [new(symbol, frame.Market.AvailableAtUtc, "normal", .8m, 30)
                    { PlannedStopPrice = 80, PlannedTargetPrice = 150, LotSize = 30 }] : [];
    }
}
