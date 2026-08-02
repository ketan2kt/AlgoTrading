using TradingSystem.Application.Broker;

namespace TradingSystem.IntegrationTests;

internal sealed class TestPaperBrokerJournal : IPaperBrokerJournal
{
    private readonly List<PaperBrokerJournalEntry> _entries = [];

    public Task<IReadOnlyList<PaperBrokerJournalEntry>> ReadAllAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<PaperBrokerJournalEntry>>(_entries.ToArray());
    }

    public Task AppendAsync(
        PaperBrokerJournalEntry entry,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _entries.Add(entry);
        return Task.CompletedTask;
    }
}
