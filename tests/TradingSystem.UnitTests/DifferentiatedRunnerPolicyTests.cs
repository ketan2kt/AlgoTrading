using System.Text.Json;
using TradingSystem.Application.Execution;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class DifferentiatedRunnerPolicyTests
{
    [Fact]
    public void UnreducedRunnerNeedsHalfRiskProfitAndCurrentConfirmation()
    {
        Assert.True(DifferentiatedRunnerPolicy.Eligible(true, 150, 150, false, true, .50m));
        Assert.False(DifferentiatedRunnerPolicy.Eligible(true, 150, 150, false, true, .49m));
        Assert.False(DifferentiatedRunnerPolicy.Eligible(true, 150, 150, false, false, 2m));
        Assert.False(DifferentiatedRunnerPolicy.Eligible(false, 150, 150, false, true, 2m));
        Assert.False(DifferentiatedRunnerPolicy.Eligible(true, 150, 225, false, true, 2m));
        Assert.True(DifferentiatedRunnerPolicy.AllowsTargetExtension(false, true));
        Assert.False(DifferentiatedRunnerPolicy.AllowsTargetExtension(false, false));
    }

    [Theory]
    [InlineData("nifty", "NIFTY", Direction.Buy)]
    [InlineData("sensex", "SENSEX", Direction.Buy)]
    [InlineData("bank-nifty", "BANKNIFTY", Direction.Buy)]
    [InlineData("nifty", "NIFTY", Direction.Sell)]
    [InlineData("sensex", "SENSEX", Direction.Sell)]
    [InlineData("bank-nifty", "BANKNIFTY", Direction.Sell)]
    public void CurrentTrendQualifiesBeforePartialDespiteTransitionEntryWithoutLoweringSavedStop(
        string market, string underlying, Direction direction)
    {
        var start = new DateTimeOffset(2026, 11, 6, 4, 0, 0, TimeSpan.Zero);
        var now = start.AddMinutes(22);
        var sign = direction == Direction.Buy ? 1m : -1m;
        var symbol = underlying + (direction == Direction.Buy ? "CE" : "PE");
        var bars = Enumerable.Range(0, 22).Select(i =>
        {
            var open = 22000m + sign * i * 8m;
            var close = open + sign * 8m;
            return new HistoricalReplayBar(start.AddMinutes(i), 60, open,
                Math.Max(open, close) + 1, Math.Min(open, close) - 1, close) { Symbol = underlying };
        }).ToArray();
        var quotes = new Dictionary<string, HistoricalReplayQuote>
            { [underlying] = new(underlying, now, bars[^1].Close), [symbol] = new(symbol, now, 110.4m) };
        var position = new HistoricalReplayOpenPosition(symbol, "transition setup", start.AddMinutes(18),
            100m, 110.4m, 150, 90m, 110m)
        {
            OriginalQuantity = 150, BaseQuantity = 150, InitialStopPrice = 90m, InitialTargetPrice = 110m,
            LotSize = 75, UnderlyingDirection = direction, SignalConfidence = .8m,
            SignalRegime = direction == Direction.Buy ? MarketRegime.WeakBullishTrend : MarketRegime.WeakBearishTrend,
            DecisionAtUtc = start.AddMinutes(18), HighestDecisionPrice = 110.4m
        };
        HistoricalReplayPortfolioFrame Frame(HistoricalReplayOpenPosition p) =>
            new(new(now, bars, quotes), p, []) { OpenPositions = [p] };
        IHistoricalReplayPortfolioPolicy Policy(bool enabled) => market == "nifty"
            ? new HistoricalNiftyPositionPolicy(new NoEntries(), new(new TimeOnly(15, 15), true, true, true,
                true, .6m, true, .55m, 3, true, .8m, 30, 30, 60, enabled))
            : new HistoricalMarketOptionPositionPolicy(new NoEntries(), market, underlying,
                new(new TimeOnly(15, 15), true, true, true, .55m, 3, true, .8m, 30, 30, 60, enabled),
                [new(Guid.Empty, symbol, direction == Direction.Buy ? InstrumentType.CallOption : InstrumentType.PutOption,
                    new DateOnly(2026, 11, 12), 22000m, 75, .05m)]);
        var normal = Policy(false);
        var runner = Policy(true);
        Assert.False(normal.ShouldExtendTarget(Frame(position), position));
        Assert.True(runner.ShouldExtendTarget(Frame(position), position));
        var normalStop = Assert.Single(normal.EvaluateStopUpdates(Frame(position))).NewStopPrice;
        var runnerStop = Assert.Single(runner.EvaluateStopUpdates(Frame(position))).NewStopPrice;
        Assert.InRange(runnerStop, 100m, normalStop);
        if (market == "nifty")
        {
            // Both modes must keep at least 70% of the ₹10.40 peak gain.
            Assert.Equal(107.28m, runnerStop);
            Assert.Equal(runnerStop, normalStop);
        }
        else
        {
            Assert.True(runnerStop < normalStop);
        }
        var alreadyProtected = position with { StopPrice = 108m };
        Assert.All(runner.EvaluateStopUpdates(Frame(alreadyProtected)), update => Assert.True(update.NewStopPrice >= 108m));
        quotes.Remove(underlying);
        Assert.False(runner.ShouldExtendTarget(Frame(position), position));
    }

    [Fact]
    public void FastEmaPullbackKeepsRunnerButSlowEmaBreakOrMissingBarsDoesNot()
    {
        var start = new DateTimeOffset(2026,11,6,4,0,0,TimeSpan.Zero);
        var bars = Enumerable.Range(0,21).Select(i => new TradingSystem.Application.Strategies.StrategyPriceBar(
            start.AddMinutes(i),22000m+i*8,22009m+i*8,21999m+i*8,22008m+i*8)).ToArray();
        bars[^1] = bars[^1] with { Close = 22120m, Low = 22119m };
        var assessment = UnderlyingTrendInvalidationPolicy.Evaluate(Direction.Buy,bars,22130m,22100m);
        Assert.False(assessment.DirectionAligned);
        Assert.True(assessment.RunnerStructureIntact);
        Assert.False(assessment.ShouldExit);
        var broken = bars.ToArray();
        broken[^1] = broken[^1] with { Close = 22090m, Low = 22089m };
        Assert.False(UnderlyingTrendInvalidationPolicy.Evaluate(Direction.Buy,broken,22130m,22100m).RunnerStructureIntact);
        bars[10] = bars[10] with { OpenTimeUtc = bars[10].OpenTimeUtc.AddSeconds(1) };
        Assert.False(UnderlyingTrendInvalidationPolicy.Evaluate(Direction.Buy,bars,22130m,22100m).RunnerStructureIntact);
    }

    [Theory]
    [InlineData(true, 150, 75, true, true, true)]
    [InlineData(false, 150, 75, true, true, false)]
    [InlineData(true, 150, 150, false, true, false)]
    [InlineData(true, 150, 0, true, true, false)]
    [InlineData(true, 150, 75, true, false, false)]
    [InlineData(true, 150, 225, true, true, false)]
    public void OnlyBankedReducedPositionsWithCurrentConfirmationQualify(bool enabled, int original,
        int remaining, bool partial, bool confirmed, bool expected) =>
        Assert.Equal(expected, DifferentiatedRunnerPolicy.Eligible(enabled, original, remaining, partial, confirmed));

    [Theory]
    [InlineData("nifty", "NIFTY", Direction.Buy)]
    [InlineData("sensex", "SENSEX", Direction.Buy)]
    [InlineData("bank-nifty", "BANKNIFTY", Direction.Buy)]
    [InlineData("nifty", "NIFTY", Direction.Sell)]
    [InlineData("sensex", "SENSEX", Direction.Sell)]
    [InlineData("bank-nifty", "BANKNIFTY", Direction.Sell)]
    public void AllAdaptersGiveConfirmedRemainderRoomButNeverLoosenExistingStop(
        string market, string underlying, Direction direction)
    {
        var start = new DateTimeOffset(2026, 11, 6, 4, 0, 0, TimeSpan.Zero);
        var now = start.AddMinutes(22);
        var symbol = underlying + (direction == Direction.Buy ? "CE" : "PE");
        var sign = direction == Direction.Buy ? 1m : -1m;
        var bars = Enumerable.Range(0, 22).Select(i => {
            var open = 22000m + sign * i * 8m;
            var close = open + sign * 8m;
            return new HistoricalReplayBar(start.AddMinutes(i), 60, open, Math.Max(open, close) + 1,
                Math.Min(open, close) - 1, close) { Symbol = underlying };
        }).ToArray();
        var quotes = new Dictionary<string, HistoricalReplayQuote>
        { [underlying] = new(underlying, now, bars[^1].Close), [symbol] = new(symbol, now, 130m) };
        var position = new HistoricalReplayOpenPosition(symbol, "Index momentum breakout",
            start.AddMinutes(18), 100m, 130m, 75, 110m, 110m)
        { OriginalQuantity = 150, BaseQuantity = 150, InitialStopPrice = 90m, InitialTargetPrice = 110m,
            LotSize = 75, UnderlyingDirection = direction, SignalConfidence = .8m,
            SignalRegime = direction == Direction.Buy ? MarketRegime.StrongBullishTrend : MarketRegime.StrongBearishTrend,
            DecisionAtUtc = start.AddMinutes(18), HighestDecisionPrice = 130m };
        var frame = new HistoricalReplayPortfolioFrame(new(now, bars, quotes), position, []) { OpenPositions = [position] };
        IHistoricalReplayPortfolioPolicy Policy(bool enabled) => market == "nifty"
            ? new HistoricalNiftyPositionPolicy(new NoEntries(), new(new TimeOnly(15,15), true,true,true,true,
                .6m,true,.55m,3,true,.8m,30,30,60,enabled))
            : new HistoricalMarketOptionPositionPolicy(new NoEntries(), market, underlying,
                new(new TimeOnly(15,15),true,true,true,.55m,3,true,.8m,30,30,60,enabled),
                [new(Guid.Empty,symbol,direction == Direction.Buy ? InstrumentType.CallOption : InstrumentType.PutOption,
                    new DateOnly(2026,11,12),22000m,75,.05m)]);
        var baseline = Policy(false);
        var runner = Policy(true);
        Assert.Equal(122.5m, Assert.Single(baseline.EvaluateStopUpdates(frame)).NewStopPrice);
        var expectedRunnerStop = market == "nifty" ? 121m : 115m;
        Assert.Equal(expectedRunnerStop, Assert.Single(runner.EvaluateStopUpdates(frame)).NewStopPrice);
        var tighter = position with { StopPrice = 124m };
        Assert.Empty(runner.EvaluateStopUpdates(frame with { OpenPosition = tighter, OpenPositions = [tighter] }));
        var unbanked = position with { Quantity = 150 };
        Assert.Equal(expectedRunnerStop, Assert.Single(runner.EvaluateStopUpdates(frame with
            { OpenPosition = unbanked, OpenPositions = [unbanked] })).NewStopPrice);
        quotes[underlying] = quotes[underlying] with { AvailableAtUtc = now.AddSeconds(-31) };
        Assert.Equal(122.5m, Assert.Single(runner.EvaluateStopUpdates(frame)).NewStopPrice);
    }

    [Fact]
    public void LegacyReceiptsDefaultOffWithoutChangingSerializedShape()
    {
        var settings = JsonSerializer.Deserialize<HistoricalMarketOptionExitSettings>("{}")!;
        Assert.False(settings.DifferentiatedRunnerEnabled);
        Assert.DoesNotContain("DifferentiatedRunnerEnabled", JsonSerializer.Serialize(settings), StringComparison.Ordinal);
        Assert.Contains("DifferentiatedRunnerEnabled", JsonSerializer.Serialize(settings with
            { DifferentiatedRunnerEnabled = true }), StringComparison.Ordinal);
    }

    private sealed class NoEntries : IHistoricalReplayPortfolioPolicy
    {
        public string CodeVersion => "runner-test";
        public IReadOnlyList<HistoricalReplayCandidate> Evaluate(HistoricalReplayPortfolioFrame frame) => [];
    }
}
