using TradingSystem.Application.Broker;

namespace TradingSystem.UnitTests;

internal sealed class TestPaperBrokerJournal : IPaperBrokerJournal
{
    private readonly object _syncRoot = new();
    private readonly List<PaperBrokerJournalEntry> _entries = [];

    public bool FailNextAppend { get; set; }
    public bool CommitThenFailNextAppend { get; set; }

    public IReadOnlyList<PaperBrokerJournalEntry> Entries
    {
        get
        {
            lock (_syncRoot)
            {
                return _entries.ToArray();
            }
        }
    }

    public Task<IReadOnlyList<PaperBrokerJournalEntry>> ReadAllAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Entries);
    }

    public Task AppendAsync(
        PaperBrokerJournalEntry entry,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (FailNextAppend)
        {
            FailNextAppend = false;
            throw new InvalidOperationException("Simulated durable journal failure.");
        }

        lock (_syncRoot)
        {
            if (_entries.Any(value => value.Sequence == entry.Sequence))
            {
                throw new InvalidOperationException("Duplicate paper journal sequence.");
            }

            _entries.Add(entry);
        }

        if (CommitThenFailNextAppend)
        {
            CommitThenFailNextAppend = false;
            throw new InvalidOperationException("Simulated unknown journal commit outcome.");
        }

        return Task.CompletedTask;
    }
}
