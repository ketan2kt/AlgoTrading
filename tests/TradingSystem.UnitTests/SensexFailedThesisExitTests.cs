using TradingSystem.Application.Execution;
using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class SensexFailedThesisExitTests
{
    private static readonly DateTimeOffset Entry = new(2026, 11, 5, 8, 59, 3, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 9, 2, 0, TimeSpan.Zero);
    private const string Strategy = "Sensex persistent directional thesis";

    // Recorded spot candles, not synthetic option fills. The 670 option mark below
    // is a conditional adverse-gate test; no historical exit saving is inferred.
    internal static StrategyPriceBar[] RecordedBars()
    {
        decimal[][] values = [
            [79278.82m,79299.58m,79272.05m,79292.38m],
            [79287.12m,79289.22m,79248.34m,79248.34m],
            [79252.82m,79252.82m,79221.36m,79221.36m],
            [79232.18m,79241.44m,79220.17m,79223.21m],
            [79213.39m,79254.09m,79210.90m,79250.58m],
            [79257.49m,79267.94m,79254.12m,79255.39m],
            [79253.67m,79267.27m,79247.73m,79260.82m],
            [79263.84m,79281.48m,79263.84m,79269.47m],
            [79266.70m,79266.70m,79220.14m,79220.23m],
            [79222.41m,79262.18m,79222.41m,79256.30m],
            [79254.34m,79271.30m,79249.00m,79271.30m],
            [79284.75m,79286.01m,79280.78m,79285.29m]];
        return values.Select((v, i) => new StrategyPriceBar(Now.AddMinutes(i - 12), v[0], v[1], v[2], v[3])).ToArray();
    }

    private static FailedSetupExitDecision Evaluate(IReadOnlyList<StrategyPriceBar>? bars = null,
        string market = "sensex", string strategy = Strategy, decimal option = 670m,
        decimal peak = 700.20m, decimal spot = 79274.49m, DateTimeOffset? quoteAt = null) =>
        FailedSetupExitPolicy.EvaluateForMarket(market, strategy, Direction.Sell,
            696.35m, option, 69.65m, peak, Entry, Now, spot, quoteAt ?? Now,
            bars ?? RecordedBars(), 60, 5);

    [Fact]
    public void RecordedReclaimAllowsExitWithoutDemandingAnotherNewHigh()
    {
        Assert.False(FailedSetupExitPolicy.Evaluate(Direction.Sell, 696.35m, 670m,
            69.65m, 700.20m, Entry, Now, 79274.49m, RecordedBars()).ShouldExit);
        var decision = Evaluate();
        Assert.True(decision.ShouldExit);
        Assert.Contains("Entry-candle reclaim", Assert.Single(decision.Reasons));
    }

    [Theory]
    [InlineData("nifty", Strategy)]
    [InlineData("sensex", "Hero Zero|example")]
    [InlineData("sensex", "Expansion Pair|example")]
    [InlineData("bank-nifty", "PaperOnly|Pyramid|example")]
    public void PathsOutsideRegularSensexAndBankNiftyDoNotUseThisReclaimExit(string market, string strategy) =>
        Assert.False(Evaluate(market: market, strategy: strategy).ShouldExit);

    [Theory]
    [InlineData(690, 700.20, 79274.49)] // Small option pullback.
    [InlineData(670, 731.175, 79274.49)] // A prior +0.5R winner is protected elsewhere.
    [InlineData(670, 700.20, 79265)] // Fresh quote no longer holds reclaimed boundary.
    public void NormalPullbackAndPriorWinnerStayWithExistingProtection(decimal option, decimal peak, decimal spot) =>
        Assert.False(Evaluate(option: option, peak: peak, spot: spot).ShouldExit);

    [Fact]
    public void OneCandleWicksAndNonOpposingCandlesCannotConfirmReclaim()
    {
        var bars = RecordedBars();
        Assert.False(Evaluate(bars[..^1], spot: 79272m).ShouldExit);
        bars[^2] = bars[^2] with { Close = 79265m };
        Assert.False(Evaluate(bars).ShouldExit); // Wick beyond entry high is insufficient.
        bars = RecordedBars();
        bars[^1] = bars[^1] with { Open = 79286m };
        Assert.False(Evaluate(bars).ShouldExit);
    }

    [Fact]
    public void MissingStaleFutureOrGappedEvidenceCannotConfirmExit()
    {
        Assert.False(Evaluate(quoteAt: Now.AddSeconds(-6)).ShouldExit);
        Assert.False(Evaluate(quoteAt: Now.AddSeconds(1)).ShouldExit);
        var bars = RecordedBars();
        bars[^1] = bars[^1] with { OpenTimeUtc = Now };
        Assert.False(Evaluate(bars).ShouldExit);
        bars = RecordedBars();
        bars[9] = bars[9] with { OpenTimeUtc = bars[9].OpenTimeUtc.AddSeconds(1) };
        Assert.False(Evaluate(bars).ShouldExit);
        Assert.False(Evaluate(RecordedBars()[..8]).ShouldExit);
    }

    [Fact]
    public void CallSideUsesSymmetricSignalLowReclaim()
    {
        var bars = RecordedBars().Select(b => b with { Open = 160000m - b.Open,
            High = 160000m - b.Low, Low = 160000m - b.High, Close = 160000m - b.Close }).ToArray();
        Assert.True(FailedSetupExitPolicy.EvaluateForMarket("sensex", Strategy, Direction.Buy,
            696.35m, 670m, 69.65m, 700.20m, Entry, Now, 160000m - 79274.49m, Now,
            bars, 60, 5).ShouldExit);
    }

    [Fact]
    public void ReplayUsesSharedReclaimAndPreservesStopPriority()
    {
        const string symbol = "SENSEX26N1279300PE";
        var position = new HistoricalReplayOpenPosition(symbol, Strategy, Entry, 696.35m, 670m,
            100, 626.70m, 766m) { OriginalQuantity = 100, InitialStopPrice = 626.70m,
                InitialTargetPrice = 766m, LotSize = 20, UnderlyingDirection = Direction.Sell };
        var bars = RecordedBars().Select(b => new HistoricalReplayBar(b.OpenTimeUtc, 60,
            b.Open, b.High, b.Low, b.Close) { Symbol = "SENSEX" }).ToArray();
        var frame = new HistoricalReplayPortfolioFrame(new(Now, bars,
            new Dictionary<string, HistoricalReplayQuote> { [symbol] = new(symbol, Now, 670m),
                ["SENSEX"] = new("SENSEX", Now, 79274.49m) }), position, []) { OpenPositions = [position] };
        var policy = new HistoricalMarketOptionPositionPolicy(new NoEntries(), "sensex", "SENSEX",
            new(new TimeOnly(15, 15), true, true, true, .55m, 3, true, .8m, 5, 5, 60),
            [new(Guid.Empty, symbol, InstrumentType.PutOption, new DateOnly(2026, 11, 12), 79300m, 20, .05m)]);
        Assert.Equal("FailedSetupExit", Assert.Single(policy.EvaluateExits(frame)).Reason);
        var stopped = position with { StopPrice = 675m };
        Assert.Equal("StopLossHit", Assert.Single(policy.EvaluateExits(frame with
            { OpenPosition = stopped, OpenPositions = [stopped] })).Reason);
    }

    private sealed class NoEntries : IHistoricalReplayPortfolioPolicy
    {
        public string CodeVersion => "fixture-only";
        public IReadOnlyList<HistoricalReplayCandidate> Evaluate(HistoricalReplayPortfolioFrame frame) => [];
    }
}
