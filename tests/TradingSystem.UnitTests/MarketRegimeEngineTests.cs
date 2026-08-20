using TradingSystem.Application.Regime;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class MarketRegimeEngineTests
{
    [Fact]
    public void NeutralOrCompressionRegimeIsNotDirectionallyTradable()
    {
        var result = new MarketRegimeEngine(new()).Evaluate(Input(
            open: 100m, current: 100m, vwap: 100m, fast: 100m, slow: 100m,
            atr: 0.5m, relativeVolume: 1m));

        Assert.True(result.Confidence >= 0.50m);
        Assert.False(result.TradingPermitted);
    }

    private readonly MarketRegimeEngine engine = new(new MarketRegimeOptions());

    [Theory]
    [InlineData(101, 102, 101, 100, 100, 0.5, 1.2, MarketRegime.GapUpContinuation, Direction.Buy)]
    [InlineData(101, 99, 100, 100, 100, 0.5, 1.2, MarketRegime.GapUpRejection, Direction.Sell)]
    [InlineData(99, 98, 99, 100, 100, 0.5, 1.2, MarketRegime.GapDownContinuation, Direction.Sell)]
    [InlineData(99, 101, 100, 100, 100, 0.5, 1.2, MarketRegime.GapDownReversal, Direction.Buy)]
    [InlineData(100, 101, 100, 101, 100, 0.5, 1.2, MarketRegime.StrongBullishTrend, Direction.Buy)]
    [InlineData(100, 99, 100, 99, 100, 0.5, 1.2, MarketRegime.StrongBearishTrend, Direction.Sell)]
    [InlineData(100, 100, 100, 100, 100, 1.0, 2.0, MarketRegime.HighVolatilityExpansion, null)]
    [InlineData(100, 100, 100, 100, 100, 0.1, 1.0, MarketRegime.LowVolatilityCompression, null)]
    public void ClassifiesRepresentativeRegimes(decimal open, decimal current, decimal vwap,
        decimal fast, decimal slow, decimal atr, decimal relativeVolume, MarketRegime expected,
        Direction? bias)
    {
        var result = engine.Evaluate(Input(open, current, vwap, fast, slow, atr, relativeVolume));

        Assert.Equal(expected, result.Regime);
        Assert.Equal(bias, result.DirectionalBias);
        Assert.NotEmpty(result.SupportingFactors);
    }

    [Fact]
    public void LowQualityOrBlockedDataReturnsUncertainAndProhibitsTrading()
    {
        var result = engine.Evaluate(Input() with
        {
            DataQuality = 0.80m,
            MarketDataTradingPermitted = false
        });

        Assert.Equal(MarketRegime.Uncertain, result.Regime);
        Assert.False(result.TradingPermitted);
        Assert.Equal(2, result.ContradictingFactors.Count);
    }

    [Fact]
    public void WeakTrendRecordsContradictingVwapEvidence()
    {
        var result = engine.Evaluate(Input(current: 99.9m, vwap: 100m, fast: 100.1m, slow: 100m));

        Assert.Equal(MarketRegime.WeakBullishTrend, result.Regime);
        Assert.Contains(result.ContradictingFactors, factor => factor.Contains("VWAP", StringComparison.Ordinal));
        Assert.False(result.TradingPermitted);
    }

    [Fact]
    public async Task HistoricalReplayPersistsEveryExplainableDecision()
    {
        var persistence = new StubPersistence();
        var monitor = new MarketRegimeMonitor();
        var service = new MarketRegimeService(engine, persistence, monitor);
        var session = Guid.NewGuid();
        var replay = new[]
        {
            Input(sessionId: session, current: 100m, atr: 0.1m),
            Input(sessionId: session, current: 101m, fast: 101m, slow: 100m),
            Input(sessionId: session, current: 100m, atr: 1m, relativeVolume: 2m)
        };

        foreach (var value in replay)
            await service.EvaluateAsync(value, CancellationToken.None);

        Assert.Equal(3, persistence.Results.Count);
        Assert.Equal(MarketRegime.HighVolatilityExpansion, monitor.GetLatest()!.Regime);
        Assert.All(persistence.Results, result => Assert.NotEmpty(result.SupportingFactors));
    }

    private static MarketRegimeInput Input(decimal open = 100m, decimal current = 100m,
        decimal vwap = 100m, decimal fast = 100m, decimal slow = 100m, decimal atr = 0.5m,
        decimal relativeVolume = 1m, Guid? sessionId = null) => new(
            sessionId ?? Guid.NewGuid(), DateTimeOffset.UtcNow, current, 100m, open,
            101m, 99m, vwap, fast, slow, atr, relativeVolume, 0.98m, true);

    private sealed class StubPersistence : IMarketRegimePersistence
    {
        public List<MarketRegimeResult> Results { get; } = [];
        public Task PersistAsync(Guid tradingSessionId, MarketRegimeResult result,
            CancellationToken cancellationToken)
        { Results.Add(result); return Task.CompletedTask; }
    }
}
