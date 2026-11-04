using Microsoft.EntityFrameworkCore;
using TradingSystem.Application.Diagnostics;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.Infrastructure.Diagnostics;

internal sealed class EfEngineEvidenceReader(TradingDbContext db) : IEngineEvidenceReader
{
    internal static IQueryable<SystemEvent> Query(TradingDbContext db, DateOnly date, string market)
    {
        var normalized = EfDailyTradingDiagnosticsReader.NormalizeMarket(market);
        var start = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(5.5)).ToUniversalTime();
        var end = start.AddDays(1);
        return db.SystemEvents.AsNoTracking().Where(value => value.OccurredAtUtc >= start &&
            value.OccurredAtUtc < end &&
            ((normalized == "ALL" && value.EventType.StartsWith("EngineEvidence.")) ||
             (normalized == "NIFTY" && value.EventType.StartsWith("EngineEvidence.nifty.")) ||
             (normalized == "SENSEX" && value.EventType.StartsWith("EngineEvidence.sensex.")) ||
             (normalized == "BANKNIFTY" && value.EventType.StartsWith("EngineEvidence.bank-nifty.")) ||
             (normalized == "BANKEX" && value.EventType.StartsWith("EngineEvidence.bankex."))));
    }

    public async Task<EngineEvidencePage> ReadAsync(DateOnly tradingDate, string market,
        int offset, int limit, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (offset > 100_000 || limit is < 1 or > 500)
            throw new ArgumentOutOfRangeException(nameof(limit));
        var rows = await Query(db, tradingDate, market).OrderBy(value => value.OccurredAtUtc)
            .ThenBy(value => value.Id).Skip(offset).Take(limit + 1)
            .Select(value => new DailyTradingSystemEvent(value.OccurredAtUtc,
                value.Severity, value.EventType, value.Message, value.DetailsJson))
            .ToListAsync(cancellationToken);
        return new(tradingDate, EfDailyTradingDiagnosticsReader.NormalizeMarket(market), offset,
            rows.Count > limit ? offset + limit : null, rows.Take(limit).ToArray());
    }
}
