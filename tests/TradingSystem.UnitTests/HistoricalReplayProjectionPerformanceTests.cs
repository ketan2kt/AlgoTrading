using System.Diagnostics;
using System.Text.Json;
using TradingSystem.Application.Execution;
using TradingSystem.Domain.Trading;
using Xunit.Abstractions;

namespace TradingSystem.UnitTests;

public sealed class HistoricalReplayProjectionPerformanceTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset Start = new(2026, 11, 16, 9, 15, 0, TimeSpan.FromHours(5.5));

    [Theory]
    [InlineData("NIFTY")]
    [InlineData("SENSEX")]
    [InlineData("BANKNIFTY")]
    public void CachedFramesMatchColdFramesAcrossFullDayAndTimeRewinds(string market)
    {
        var symbol = market + "CE";
        var contract = new NiftyOptionContractCandidate(Guid.Empty, symbol, InstrumentType.CallOption,
            new(2026, 11, 23), 55000, 30, .05m);
        IHistoricalReplayPortfolioPolicy Create() => market == "NIFTY"
            ? new HistoricalNiftyPositionPolicy(new NoEntries(), new(new(15, 15), true, true, true,
                true, .6m, true, .55m, 3, true, .8m, 30, 30, 60))
            : new HistoricalMarketOptionPositionPolicy(new NoEntries(), market == "SENSEX" ? "sensex" : "bank-nifty", market,
                new(new(15, 15), true, true, true, .55m, 3, true, .8m, 30, 30, 60), [contract]);
        var bars = Enumerable.Range(-40, 415).Select(i =>
        {
            var price = 55000m + (i % 45 < 25 ? i % 45 * 5m : (45 - i % 45) * 5m);
            return new HistoricalReplayBar(Start.AddMinutes(i), 60, price, price + 6, price - 6, price + 2)
                { Symbol = market };
        }).ToArray();
        var position = new HistoricalReplayOpenPosition(symbol, "Index momentum breakout", Start.AddMinutes(5),
            100, 105, 150, 90, 120)
        {
            OriginalQuantity = 150, BaseQuantity = 150, InitialStopPrice = 90, InitialTargetPrice = 120,
            UnderlyingDirection = Direction.Buy, DecisionAtUtc = Start.AddMinutes(5), LotSize = 30,
            SignalConfidence = .8m, SignalRegime = MarketRegime.StrongBullishTrend, HighestDecisionPrice = 107
        };
        // Full immutable array intentionally includes not-yet-completed bars:
        // crossing a close must invalidate the cache without future leakage.
        var frames = Enumerable.Range(0, 7500).Select(i =>
        {
            var at = Start.AddSeconds(i * 3);
            var quotes = new Dictionary<string, HistoricalReplayQuote>
            {
                [market] = new(market, at, 55050),
                [symbol] = new(symbol, at, 100 + i % 17) { SourceAtUtc = at }
            };
            return new HistoricalReplayPortfolioFrame(new(at, bars, quotes), position, []) { OpenPositions = [position] };
        }).Concat(new[] { new HistoricalReplayPortfolioFrame(new(Start.AddMinutes(12), bars,
            new Dictionary<string, HistoricalReplayQuote> { [symbol] = new(symbol, Start.AddMinutes(12), 95) }),
            position, []) { OpenPositions = [position] } }).ToArray();
        string[] Run(bool cold, out double milliseconds, out long allocated)
        {
            var policy = Create();
            var before = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew();
            var results = frames.Select(frame =>
            {
                // The cold control rebuilds identical immutable snapshots per
                // operation, disabling both caches without altering any policy.
                HistoricalReplayPortfolioFrame Input() => cold ? frame with
                    { Market = frame.Market with { CompletedBars = frame.Market.CompletedBars.ToArray() } } : frame;
                return JsonSerializer.Serialize(new
                {
                    Stop = policy.EvaluateStopUpdates(Input()), Exit = policy.EvaluateExits(Input()),
                    Partial = policy.EvaluatePartialExits(Input()), Runner = policy.ShouldExtendTarget(Input(), position)
                });
            }).ToArray();
            milliseconds = watch.Elapsed.TotalMilliseconds;
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            return results;
        }
        _ = Create().EvaluateStopUpdates(frames[30]);
        var cold = Run(true, out var coldMs, out var coldBytes);
        var cached = Run(false, out var cachedMs, out var cachedBytes);
        Assert.Equal(cold, cached);
        Assert.True(cachedBytes < coldBytes, "Immutable projections must reduce allocated work.");
        output.WriteLine($"{market}: {frames.Length:N0} quote frames, cold {coldMs:F0} ms / {coldBytes / 1_000_000d:F1} MB; cached {cachedMs:F0} ms / {cachedBytes / 1_000_000d:F1} MB; all decisions identical.");
    }

    private sealed class NoEntries : IHistoricalReplayPortfolioPolicy
    {
        public string CodeVersion => "projection-performance-fixture";
        public IReadOnlyList<HistoricalReplayCandidate> Evaluate(HistoricalReplayPortfolioFrame frame) => [];
    }
}
