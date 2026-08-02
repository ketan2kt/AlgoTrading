namespace TradingSystem.Application.Broker;

/// <summary>
/// Append-only durable journal used to reconstruct the simulated broker after a process restart.
/// </summary>
public interface IPaperBrokerJournal
{
    Task<IReadOnlyList<PaperBrokerJournalEntry>> ReadAllAsync(
        CancellationToken cancellationToken);

    Task AppendAsync(
        PaperBrokerJournalEntry entry,
        CancellationToken cancellationToken);
}

public sealed record PaperBrokerJournalEntry(
    Guid Id,
    long Sequence,
    string EventType,
    string ClientReference,
    string PayloadJson,
    DateTimeOffset OccurredAtUtc);
