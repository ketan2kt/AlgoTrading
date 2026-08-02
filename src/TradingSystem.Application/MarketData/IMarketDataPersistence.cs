namespace TradingSystem.Application.MarketData;

public interface IMarketDataPersistence
{
    Task PersistObservationAsync(MarketObservation observation, CancellationToken cancellationToken);

    Task PersistCandleAsync(CompletedCandle candle, CancellationToken cancellationToken);
    Task RecordProviderHealthAsync(string provider, MarketDataValidationResult result,
        DateTimeOffset observedAtUtc, CancellationToken cancellationToken);
}
