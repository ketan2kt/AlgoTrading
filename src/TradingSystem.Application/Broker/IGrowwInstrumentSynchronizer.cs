namespace TradingSystem.Application.Broker;

public interface IGrowwInstrumentSynchronizer
{
    Task<GrowwInstrumentSyncResult> SynchronizeAsync(CancellationToken cancellationToken);
}

public sealed record GrowwInstrumentSyncResult(
    int Downloaded,
    int Inserted,
    int Updated,
    int Skipped,
    DateTimeOffset CompletedAtUtc);
