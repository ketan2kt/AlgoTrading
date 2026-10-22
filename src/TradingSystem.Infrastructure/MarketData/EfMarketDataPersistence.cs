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
        var id = Guid.NewGuid();
        var now = timeProvider.GetUtcNow();
        var concurrencyToken = Guid.NewGuid();
        if (result.Accepted)
        {
            await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO trading.provider_health
                    ("Id", "Provider", "IsAvailable", "LastSuccessAtUtc", "LastFailureAtUtc",
                     "ErrorCode", "CreatedAtUtc", "UpdatedAtUtc", "ConcurrencyToken")
                VALUES ({id}, {provider}, TRUE, {observedAtUtc}, NULL, NULL, {now}, {now}, {concurrencyToken})
                ON CONFLICT ("Provider") DO UPDATE SET
                    "IsAvailable" = TRUE,
                    "LastSuccessAtUtc" = EXCLUDED."LastSuccessAtUtc",
                    "ErrorCode" = NULL,
                    "UpdatedAtUtc" = EXCLUDED."UpdatedAtUtc",
                    "ConcurrencyToken" = EXCLUDED."ConcurrencyToken";
                """, cancellationToken);
            return;
        }

        var errorCode = result.Reason.ToString();
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO trading.provider_health
                ("Id", "Provider", "IsAvailable", "LastSuccessAtUtc", "LastFailureAtUtc",
                 "ErrorCode", "CreatedAtUtc", "UpdatedAtUtc", "ConcurrencyToken")
            VALUES ({id}, {provider}, FALSE, NULL, {observedAtUtc}, {errorCode}, {now}, {now}, {concurrencyToken})
            ON CONFLICT ("Provider") DO UPDATE SET
                "IsAvailable" = FALSE,
                "LastFailureAtUtc" = EXCLUDED."LastFailureAtUtc",
                "ErrorCode" = EXCLUDED."ErrorCode",
                "UpdatedAtUtc" = EXCLUDED."UpdatedAtUtc",
                "ConcurrencyToken" = EXCLUDED."ConcurrencyToken";
            """, cancellationToken);
    }
}
