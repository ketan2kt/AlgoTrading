using Microsoft.EntityFrameworkCore;
using TradingSystem.Application.MarketData;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.Infrastructure.MarketData;

internal sealed class EfMarketDataPersistence(TradingDbContext dbContext, TimeProvider timeProvider)
    : IMarketDataPersistence
{
    public async Task PersistObservationAsync(
        MarketObservation value,
        CancellationToken cancellationToken)
    {
        dbContext.MarketObservations.Add(new PersistedMarketObservation(
            Guid.NewGuid(), value.InstrumentId, value.Source, value.SourceTimestampUtc,
            value.ReceivedAtUtc, value.Price, value.VolumeDelta, value.OpenInterest));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task PersistCandleAsync(CompletedCandle value, CancellationToken cancellationToken)
    {
        dbContext.Candles.Add(new Candle(Guid.NewGuid(), value.InstrumentId, value.OpenTimeUtc,
            value.IntervalSeconds, value.Open, value.High, value.Low, value.Close, value.Volume,
            value.Source, value.OpenInterest));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task RecordProviderHealthAsync(string provider, MarketDataValidationResult result,
        DateTimeOffset observedAtUtc, CancellationToken cancellationToken)
    {
        var health = await dbContext.ProviderHealth.SingleOrDefaultAsync(value => value.Provider == provider,
            cancellationToken);
        if (health is null)
        {
            health = new ProviderHealth(Guid.NewGuid(), provider, timeProvider.GetUtcNow());
            dbContext.ProviderHealth.Add(health);
        }
        if (result.Accepted) health.RecordSuccess(observedAtUtc);
        else health.RecordFailure(result.Reason.ToString(), observedAtUtc);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
