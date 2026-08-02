using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TradingSystem.Application.Broker;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.Infrastructure.Broker;

internal sealed class EfPaperBrokerJournal(IServiceScopeFactory scopeFactory)
    : IPaperBrokerJournal
{
    public async Task<IReadOnlyList<PaperBrokerJournalEntry>> ReadAllAsync(
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        return await context.PaperBrokerEvents
            .AsNoTracking()
            .OrderBy(value => value.Sequence)
            .Select(value => new PaperBrokerJournalEntry(
                value.Id,
                value.Sequence,
                value.EventType,
                value.ClientReference,
                value.PayloadJson,
                value.OccurredAtUtc))
            .ToArrayAsync(cancellationToken);
    }

    public async Task AppendAsync(
        PaperBrokerJournalEntry entry,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        context.PaperBrokerEvents.Add(new PaperBrokerEvent(
            entry.Id,
            entry.Sequence,
            entry.EventType,
            entry.ClientReference,
            entry.PayloadJson,
            entry.OccurredAtUtc));
        await context.SaveChangesAsync(cancellationToken);
    }
}
