using Microsoft.EntityFrameworkCore;
using Npgsql;
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
        var candle = new Candle(Guid.NewGuid(), value.InstrumentId, value.OpenTimeUtc,
            value.IntervalSeconds, value.Open, value.High, value.Low, value.Close, value.Volume,
            value.Source, value.OpenInterest);
        dbContext.Candles.Add(candle);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
               { SqlState: PostgresErrorCodes.UniqueViolation,
                 ConstraintName: "IX_candles_InstrumentId_OpenTimeUtc_IntervalSeconds_Source" })
        {
            dbContext.Entry(candle).State = EntityState.Detached;
        }
    }

    public async Task RecordProviderHealthAsync(string provider, MarketDataValidationResult result,
        DateTimeOffset observedAtUtc, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var health = await dbContext.ProviderHealth.SingleOrDefaultAsync(
                value => value.Provider == provider, cancellationToken);
            if (health is null)
            {
                health = new ProviderHealth(Guid.NewGuid(), provider, timeProvider.GetUtcNow());
                dbContext.ProviderHealth.Add(health);
            }

            if (result.Accepted) health.RecordSuccess(observedAtUtc);
            else health.RecordFailure(result.Reason.ToString(), observedAtUtc);

            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateConcurrencyException) when (attempt == 0)
            {
                DetachProviderHealthEntries();
            }
            catch (DbUpdateException exception) when (attempt == 0 &&
                exception.InnerException is PostgresException
                {
                    SqlState: PostgresErrorCodes.UniqueViolation,
                    ConstraintName: "IX_provider_health_Provider"
                })
            {
                DetachProviderHealthEntries();
            }
        }

        void DetachProviderHealthEntries()
        {
            foreach (var entry in dbContext.ChangeTracker.Entries<ProviderHealth>())
                entry.State = EntityState.Detached;
        }
    }
}
