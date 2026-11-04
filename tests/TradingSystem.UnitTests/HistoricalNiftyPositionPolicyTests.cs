using TradingSystem.Application.Execution;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class HistoricalNiftyPositionPolicyTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 4, 4, 0, 0,
        TimeSpan.Zero);

    [Fact]
    public void EntryInsideCandleCountsItsLaterCompletionForReversalReadiness()
    {
        var now = Start.AddMinutes(23);
        var bars = Enumerable.Range(0, 23).Select(index =>
            new HistoricalReplayBar(Start.AddMinutes(index), 60,
                22000m - index * 5m, 22001m - index * 5m,
                21994m - index * 5m, 21995m - index * 5m)
                { Symbol = "NIFTY" }).ToArray();
        var position = new HistoricalReplayOpenPosition("NIFTY22000CE", "momentum-expansion",
            Start.AddMinutes(21.5), 100m, 95m, 150, 80m, 115m)
        {
            OriginalQuantity = 150, InitialStopPrice = 80m,
            UnderlyingDirection = Direction.Buy, DecisionAtUtc = Start.AddMinutes(21.5),
            SignalRegime = MarketRegime.StrongBullishTrend, SignalConfidence = .8m
        };
        var frame = new HistoricalReplayPortfolioFrame(new(now, bars,
            new Dictionary<string, HistoricalReplayQuote>
            {
                ["NIFTY"] = new("NIFTY", now, 21885m),
                [position.Symbol] = new(position.Symbol, now, 95m)
            }), position, []) { OpenPositions = [position] };
        var policy = new HistoricalNiftyPositionPolicy(new EntryPolicy(),
            new(new TimeOnly(15, 15), true, true, true,
                true, .6m, true, .55m, 3, true, .8m, 5, 5, 60));
        Assert.Equal("UnderlyingTrendInvalidated", Assert.Single(policy.EvaluateExits(frame)).Reason);
    }

    [Fact]
    public void ReusingPolicyAcrossRunsCannotLeakEarlierPeakIntoAnotherRun()
    {
        var settings = new HistoricalNiftyExitSettings(new TimeOnly(15, 15),
            true, true, false, false, .6m, false, .55m, 3, false, .8m,
            5, 5, 60);
        var reused = new HistoricalNiftyPositionPolicy(new EntryPolicy(), settings);
        var fresh = new HistoricalNiftyPositionPolicy(new EntryPolicy(), settings);
        var position = new HistoricalReplayOpenPosition("NIFTY22000CE",
            "momentum-expansion", Start, 100m, 105m, 150, 90m, 120m)
        {
            OriginalQuantity = 150,
            BaseQuantity = 150,
            InitialStopPrice = 90m,
            UnderlyingDirection = Direction.Buy,
            SignalRegime = MarketRegime.StrongBullishTrend,
            HighestObservedPrice = 105m
        };
        HistoricalReplayPortfolioFrame Frame(decimal mark) => new(
            new(Start.AddMinutes(2), [], new Dictionary<string,
                HistoricalReplayQuote>
            {
                [position.Symbol] = new(position.Symbol,
                    Start.AddMinutes(2), mark)
            }), position, []) { OpenPositions = [position] };

        reused.EvaluateStopUpdates(Frame(130m));
        Assert.Equal(fresh.EvaluateStopUpdates(Frame(105m)),
            reused.EvaluateStopUpdates(Frame(105m)));
    }

    [Fact]
    public void FailedSetupReadsLatestCompletedUnderlyingCandle()
    {
        var now = Start.AddMinutes(10);
        var bars = Enumerable.Range(0, 10).Select(index =>
        {
            var open = index < 8 ? 22000m : 22000m - (index - 8) * 5m;
            return new HistoricalReplayBar(Start.AddMinutes(index), 60,
                open, open + 2m, open - 12m, open - 10m)
            { Symbol = "NIFTY" };
        }).ToArray();
        var position = new HistoricalReplayOpenPosition("NIFTY22000CE",
            "momentum-expansion", Start.AddMinutes(7), 100m, 96m, 150,
            90m, 115m)
        {
            OriginalQuantity = 150,
            InitialStopPrice = 90m,
            HighestObservedPrice = 100m,
            UnderlyingDirection = Direction.Buy,
            DecisionAtUtc = Start.AddMinutes(7),
            SignalRegime = MarketRegime.RangeBound
        };
        var frame = new HistoricalReplayPortfolioFrame(new(now, bars,
            new Dictionary<string, HistoricalReplayQuote>
            {
                ["NIFTY"] = new("NIFTY", now, 21980m),
                [position.Symbol] = new(position.Symbol, now, 96m)
            }), position, []) { OpenPositions = [position] };
        var policy = new HistoricalNiftyPositionPolicy(new EntryPolicy(),
            new(new TimeOnly(15, 15), false, false, false,
                false, .6m, false, .55m, 3, false, .8m, 5, 5, 60));

        Assert.Equal("FailedSetup",
            Assert.Single(policy.EvaluateExits(frame)).Reason);
    }

    [Fact]
    public void PartialAndTargetUseSharedNiftyExitRulesAndLaterQuotes()
    {
        var policy = new HistoricalNiftyPositionPolicy(new EntryPolicy(),
            new(new TimeOnly(15, 15), true, true, true,
                false, .6m, true, .55m, 3, true, .8m, 5, 5, 60));
        var result = HistoricalPortfolioReplay.Run([
            new HistoricalReplayBar(Start, 60, 22000m, 22010m,
                21990m, 22005m) { Symbol = "NIFTY" }
        ], [
            Quote(2, 100m), Quote(3, 111m), Quote(4, 112m),
            Quote(5, 120m), Quote(6, 119m)
        ], policy, new(.10m, .15m, 0m, 0m, 10, 0),
            Start, Start.AddMinutes(8));

        Assert.Equal(2, result.Trades.Count);
        var partial = Assert.Single(result.Trades, value =>
            value.IsPartialExit);
        Assert.Equal("PartialProfit", partial.ExitReason);
        Assert.Equal(Start.AddMinutes(4), partial.ExitAtUtc);
        Assert.Equal(75, partial.Quantity);
        var remainder = Assert.Single(result.Trades, value =>
            !value.IsPartialExit);
        Assert.Equal(Start.AddMinutes(6), remainder.ExitAtUtc);
        Assert.Equal(75, remainder.Quantity);
        Assert.Equal(0, result.MissingExitQuoteCount);
    }

    [Fact]
    public void RecoveredProductionRegimeDoesNotInventADirectionalRunnerOrAddOn()
    {
        var now = Start.AddMinutes(22);
        var bars = Enumerable.Range(0, 22).Select(index =>
            new HistoricalReplayBar(Start.AddMinutes(index), 60,
                22000m + index * 8m, 22009m + index * 8m,
                21999m + index * 8m, 22008m + index * 8m)
            { Symbol = "NIFTY" }).ToArray();
        var quotes = new Dictionary<string, HistoricalReplayQuote>
        {
            ["NIFTY"] = new("NIFTY", now, 22180m),
            ["NIFTY22000CE"] = new("NIFTY22000CE", now, 120m)
        };
        var position = new HistoricalReplayOpenPosition("NIFTY22000CE",
            "momentum-expansion", Start.AddMinutes(20), 100m,
            120m, 150, 95m, 115m)
        {
            OriginalQuantity = 150,
            BaseQuantity = 150,
            InitialStopPrice = 90m,
            UnderlyingDirection = Direction.Buy,
            SignalRegime = MarketRegime.StrongBullishTrend,
            DecisionAtUtc = Start.AddMinutes(19),
            SignalConfidence = .8m,
            LotSize = 75
        };
        var frame = new HistoricalReplayPortfolioFrame(
            new(now, bars, quotes), position, [])
        { OpenPositions = [position] };
        var policy = new HistoricalNiftyPositionPolicy(new EntryPolicy(),
            new(new TimeOnly(15, 15), true, true, true,
                true, .6m, true, .55m, 3, true, .8m, 5, 5, 60));

        Assert.False(policy.ShouldExtendTarget(frame, position));
        Assert.Empty(policy.EvaluateAddOns(frame));
    }

    [Fact]
    public void OddPaperPartialIsReportedAsNonExecutableNotSilentlyCredited()
    {
        var policy = new HistoricalNiftyPositionPolicy(new EntryPolicy(75),
            new(new TimeOnly(15, 15), true, true, true,
                false, .6m, true, .55m, 3, true, .8m, 5, 5, 60));
        var result = HistoricalPortfolioReplay.Run([
            new HistoricalReplayBar(Start, 60, 22000m, 22010m,
                21990m, 22005m) { Symbol = "NIFTY" }
        ], [
            Quote(2, 100m), Quote(3, 111m), Quote(4, 112m),
            Quote(5, 120m), Quote(6, 119m)
        ], policy, new(.10m, .15m, 0m, 0m, 10, 0),
            Start, Start.AddMinutes(8));

        Assert.Equal(1, result.UnsupportedPartialCount);
        Assert.Equal(75, Assert.Single(result.Trades).Quantity);
    }

    private sealed class EntryPolicy(int quantity = 150)
        : IHistoricalReplayPortfolioPolicy
    {
        public string CodeVersion => "nifty-entry-test-v1";
        public IReadOnlyList<HistoricalReplayCandidate> Evaluate(
            HistoricalReplayPortfolioFrame frame) =>
            frame.Market.AvailableAtUtc == Start.AddMinutes(1)
                ? [new HistoricalReplayCandidate("NIFTY22000CE",
                    frame.Market.AvailableAtUtc, CodeVersion, .8m, quantity)
                {
                    LotSize = 75,
                    PlannedStopPrice = 90m,
                    PlannedTargetPrice = 115m,
                    UnderlyingDirection = Direction.Buy,
                    SignalRegime = MarketRegime.RangeBound,
                    ContractExpiryDate = new DateOnly(2026, 11, 10)
                }]
                : [];
    }

    private static HistoricalReplayQuote Quote(int minute, decimal price) =>
        new("NIFTY22000CE", Start.AddMinutes(minute), price);
}
