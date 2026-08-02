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
        CancellationToken cancellationToken)
    {
        var validation = validator.Validate(observation);
        healthMonitor.Record(observation.Source, observation.ReceivedAtUtc, validation);
        await persistence.RecordProviderHealthAsync(
            observation.Source, validation, observation.ReceivedAtUtc, cancellationToken);
        if (!validation.Accepted)
            return new(validation, null);

        await persistence.PersistObservationAsync(observation, cancellationToken);

        var completed = aggregator.Add(observation);
        if (completed is not null)
            await persistence.PersistCandleAsync(completed, cancellationToken);
        return new(validation, completed);
    }
}
