using TradingSystem.Application.MarketData;
using TradingSystem.Application.Broker;

namespace TradingSystem.UnitTests;

public sealed class MarketDataEngineTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 2, 4, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ValidatorAcceptsFreshThenRejectsDuplicateAndOutOfOrder()
    {
        var validator = new MarketDataValidator(new FixedTimeProvider(Now), TimeSpan.FromSeconds(5));
        var instrument = Guid.NewGuid();
        var first = Observation(instrument, Now.AddSeconds(-2), 10);

        Assert.True(validator.Validate(first).Accepted);
        Assert.Equal(MarketDataRejectionReason.Duplicate, validator.Validate(first).Reason);
        Assert.Equal(MarketDataRejectionReason.OutOfOrder,
            validator.Validate(Observation(instrument, Now.AddSeconds(-3), 9)).Reason);
    }

    [Fact]
    public void ValidatorRestoresPersistedCursorAndRejectsRestartDuplicate()
    {
        var validator = new MarketDataValidator(
            new FixedTimeProvider(Now),
            TimeSpan.FromSeconds(5));
        var instrument = Guid.NewGuid();
        var timestamp = Now.AddSeconds(-1);
        validator.RestoreCursor(instrument, "Groww", timestamp);

        var result = validator.Validate(new MarketObservation(
            instrument, "Groww", timestamp, Now, 100m, 1, null));

        Assert.False(result.Accepted);
        Assert.Equal(MarketDataRejectionReason.Duplicate, result.Reason);
    }

    [Fact]
    public void ValidatorRejectsStaleFutureInvalidAndSequenceGap()
    {
        var validator = new MarketDataValidator(new FixedTimeProvider(Now), TimeSpan.FromSeconds(5));
        Assert.Equal(MarketDataRejectionReason.Stale,
            validator.Validate(Observation(Guid.NewGuid(), Now.AddSeconds(-6), 1)).Reason);
        Assert.Equal(MarketDataRejectionReason.FutureTimestamp,
            validator.Validate(Observation(Guid.NewGuid(), Now.AddSeconds(2), 1)).Reason);
        Assert.Equal(MarketDataRejectionReason.Invalid,
            validator.Validate(Observation(Guid.NewGuid(), Now, 1) with { Price = 0 }).Reason);

        var instrument = Guid.NewGuid();
        Assert.True(validator.Validate(Observation(instrument, Now.AddSeconds(-2), 1)).Accepted);
        Assert.Equal(MarketDataRejectionReason.SequenceGap,
            validator.Validate(Observation(instrument, Now.AddSeconds(-1), 3)).Reason);
    }

    [Fact]
    public void GapLatchesHealthUntilExplicitReconciliationReset()
    {
        var monitor = new MarketDataHealthMonitor();
        monitor.Record("Groww", Now, new(false, false, MarketDataRejectionReason.SequenceGap, TimeSpan.Zero));
        monitor.Record("Groww", Now.AddSeconds(1), new(true, true, MarketDataRejectionReason.None, TimeSpan.Zero));

        Assert.False(Assert.Single(monitor.GetCurrent()).TradingPermitted);
        monitor.ResetAfterReconciliation("Groww");
        Assert.True(Assert.Single(monitor.GetCurrent()).TradingPermitted);
    }

    [Fact]
    public void AggregatorProducesAlignedOhlcvAndOpenInterest()
    {
        var aggregator = new CandleAggregator(60);
        var instrument = Guid.NewGuid();
        Assert.Null(aggregator.Add(Observation(instrument, Now.AddSeconds(1), 1) with
        { Price = 100m, VolumeDelta = 10, OpenInterest = 1000 }));
        Assert.Null(aggregator.Add(Observation(instrument, Now.AddSeconds(20), 2) with
        { Price = 105m, VolumeDelta = 15, OpenInterest = 1010 }));
        Assert.Null(aggregator.Add(Observation(instrument, Now.AddSeconds(40), 3) with
        { Price = 98m, VolumeDelta = 5, OpenInterest = 1020 }));

        var candle = aggregator.Add(Observation(instrument, Now.AddMinutes(1), 4) with { Price = 101m });

        Assert.NotNull(candle);
        Assert.Equal(Now, candle.OpenTimeUtc);
        Assert.Equal((100m, 105m, 98m, 98m), (candle.Open, candle.High, candle.Low, candle.Close));
        Assert.Equal(30, candle.Volume);
        Assert.Equal(1020m, candle.OpenInterest);
    }

    [Fact]
    public void IndicatorsMatchDeterministicFixtures()
    {
        Assert.Equal(4m, TechnicalIndicators.SimpleMovingAverage([1m, 2m, 3m, 4m, 5m], 3));
        Assert.Equal(4m, TechnicalIndicators.ExponentialMovingAverage([1m, 2m, 3m, 4m, 5m], 3));
        var candles = new[]
        {
            Candle(100, 102, 99, 101, 10), Candle(101, 104, 100, 103, 20),
            Candle(103, 106, 102, 105, 30)
        };
        Assert.Equal(103.0556m, Math.Round(TechnicalIndicators.VolumeWeightedAveragePrice(candles), 4));
        Assert.Equal(4m, TechnicalIndicators.AverageTrueRange(candles, 2));
    }

    [Fact]
    public void GrowwNormalizerConvertsTimestampAndCumulativeVolumeToDelta()
    {
        var normalizer = new GrowwQuoteNormalizer();
        var instrument = Guid.NewGuid();
        var first = normalizer.Normalize(instrument, Quote(100), Now);
        var second = normalizer.Normalize(instrument, Quote(125), Now.AddSeconds(1));

        Assert.Equal(0, first.VolumeDelta);
        Assert.Equal(25, second.VolumeDelta);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(Quote(0).LastTradeTimeEpochMilliseconds!.Value),
            second.SourceTimestampUtc);
    }

    [Fact]
    public void GrowwNormalizerUsesReceiptTimeWhenIndexTradeTimeIsUnavailable()
    {
        var normalizer = new GrowwQuoteNormalizer();
        var quote = Quote(0) with { LastTradeTimeEpochMilliseconds = null };

        var observation = normalizer.Normalize(Guid.NewGuid(), quote, Now);

        Assert.Equal(Now, observation.SourceTimestampUtc);
    }

    [Fact]
    public void GrowwNormalizerFailsClosedWhenCumulativeVolumeRegresses()
    {
        var normalizer = new GrowwQuoteNormalizer();
        var instrument = Guid.NewGuid();
        normalizer.Normalize(instrument, Quote(100), Now);

        Assert.Throws<InvalidOperationException>(() =>
            normalizer.Normalize(instrument, Quote(99), Now.AddSeconds(1)));
    }

    [Fact]
    public async Task ProcessorPersistsHealthAndOnlyCompletedValidatedCandles()
    {
        var persistence = new StubPersistence();
        var processor = new MarketDataProcessor(
            new MarketDataValidator(new FixedTimeProvider(Now), TimeSpan.FromSeconds(5)),
            new CandleAggregator(60), new MarketDataHealthMonitor(), persistence);
        var instrument = Guid.NewGuid();

        await processor.ProcessAsync(Observation(instrument, Now.AddSeconds(-1), 1), CancellationToken.None);
        var completed = await processor.ProcessAsync(
            Observation(instrument, Now, 2), CancellationToken.None);
        var rejected = await processor.ProcessAsync(
            Observation(instrument, Now.AddMinutes(-1), 0), CancellationToken.None);

        Assert.NotNull(completed.CompletedCandle);
        Assert.False(rejected.Validation.Accepted);
        Assert.Equal(2, persistence.Observations.Count);
        Assert.Single(persistence.Candles);
        Assert.Equal(3, persistence.HealthWrites);
    }

    private static MarketObservation Observation(Guid instrument, DateTimeOffset timestamp, long sequence) =>
        new(instrument, "Groww", timestamp, Now, 100m, 1, null, sequence);

    private static CompletedCandle Candle(decimal open, decimal high, decimal low, decimal close, long volume) =>
        new(Guid.NewGuid(), "Groww", Now, 60, open, high, low, close, volume, null);

    private static GrowwQuote Quote(decimal volume) => new(100m, Now.ToUnixTimeMilliseconds(),
        99m, 1, 101m, 1, volume, 1000m, 10m);

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class StubPersistence : IMarketDataPersistence
    {
        public List<MarketObservation> Observations { get; } = [];
        public List<CompletedCandle> Candles { get; } = [];
        public int HealthWrites { get; private set; }
        public Task PersistObservationAsync(MarketObservation observation, CancellationToken cancellationToken)
        { Observations.Add(observation); return Task.CompletedTask; }
        public Task PersistCandleAsync(CompletedCandle candle, CancellationToken cancellationToken)
        { Candles.Add(candle); return Task.CompletedTask; }
        public Task RecordProviderHealthAsync(string provider, MarketDataValidationResult result,
            DateTimeOffset observedAtUtc, CancellationToken cancellationToken)
        { HealthWrites++; return Task.CompletedTask; }
    }
}
