namespace TradingSystem.Application.MarketData;

public sealed record MarketDataProcessingResult(
    MarketDataValidationResult Validation,
    CompletedCandle? CompletedCandle);

public sealed class MarketDataProcessor(
    MarketDataValidator validator,
    CandleAggregator aggregator,
    MarketDataHealthMonitor healthMonitor,
    IMarketDataPersistence persistence)
{
    public async Task<MarketDataProcessingResult> ProcessAsync(
        MarketObservation observation,
        CancellationToken cancellationToken,
        bool aggregateCandles = true)
    {
        var validation = validator.Validate(observation);
        healthMonitor.Record(observation.Source, observation.ReceivedAtUtc, validation);
        await persistence.RecordProviderHealthAsync(
            observation.Source, validation, observation.ReceivedAtUtc, cancellationToken);
        if (!validation.Accepted)
            return new(validation, null);

        await persistence.PersistObservationAsync(observation, cancellationToken);

        // Futures minute bars come from the historical candle endpoint. Polling
        // deltas must not race those complete bars under the same source/key.
        // Observations and freshness remain recorded even when not aggregating.
        if (!aggregateCandles) return new(validation, null);

        var completed = aggregator.Add(observation);
        if (completed is not null)
            await persistence.PersistCandleAsync(completed, cancellationToken);
        return new(validation, completed);
    }
}
