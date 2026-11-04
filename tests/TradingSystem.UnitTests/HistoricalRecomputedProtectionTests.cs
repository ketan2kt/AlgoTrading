using TradingSystem.Application.Execution;

namespace TradingSystem.UnitTests;

public sealed class HistoricalRecomputedProtectionTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 4, 4, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RecomputedNiftyStopPrecedesNewQuoteBarrierAndCannotBeTreatedAsAPersistedReversalFloor()
    {
        var result = Run(new Recomputed(110));
        var trade = Assert.Single(result.Trades);
        Assert.Equal(At.AddMinutes(7), trade.ExitAtUtc);
        Assert.Equal(116, trade.ExitPrice);
    }

    [Fact]
    public void InitialRiskStillCannotBeExpandedByARecomputedProtectionUpdate() =>
        Assert.Throws<InvalidOperationException>(() => Run(new Recomputed(89)));

    private static HistoricalReplayResult Run(IHistoricalReplayPortfolioPolicy policy) => HistoricalPortfolioReplay.Run(
        [new(At, 60, 100, 101, 99, 100) { Symbol = "NIFTY" }],
        new[] { (2, 100m), (3, 120m), (4, 115m), (5, 125m), (6, 118m), (7, 116m) }
            .Select(value => new HistoricalReplayQuote("NIFTYCE", At.AddMinutes(value.Item1), value.Item2)).ToArray(),
        policy, new(.1m, .5m, 0, 0, 10, 0), At, At.AddMinutes(8));

    private sealed class Recomputed(decimal resetStop) : IHistoricalReplayPortfolioPolicy
    {
        public string CodeVersion => "nifty-recomputed-protection-fixture";
        public IReadOnlyList<HistoricalReplayCandidate> Evaluate(HistoricalReplayPortfolioFrame frame) =>
            frame.Market.AvailableAtUtc == At.AddMinutes(1) ? [new("NIFTYCE", frame.Market.AvailableAtUtc, "normal", .8m, 65)
                { RecomputesProtectionFromInitialRisk = true, PlannedStopPrice = 90, PlannedTargetPrice = 150 }] : [];
        public IReadOnlyList<HistoricalReplayStopUpdate> EvaluateStopUpdates(HistoricalReplayPortfolioFrame frame) =>
            frame.OpenPositions.Select(value => new HistoricalReplayStopUpdate(value.Symbol, frame.Market.AvailableAtUtc,
                frame.Market.AvailableAtUtc < At.AddMinutes(3) ? 90 :
                frame.Market.AvailableAtUtc < At.AddMinutes(4) ? 118 :
                frame.Market.AvailableAtUtc < At.AddMinutes(5) ? resetStop : 119)
                    { RecomputedFromInitialRisk = true }).ToArray();
    }
}
