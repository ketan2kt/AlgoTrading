using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TradingSystem.Application.Execution;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.Infrastructure.Execution;

internal sealed class SelfImprovementResearchService(IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider, ILogger<SelfImprovementResearchService> logger) : BackgroundService
{
    internal const string AuditOutcome = "SelfImprovementDaily:v1";
    private static readonly TimeZoneInfo India = FindIndiaTimeZone();
    private static readonly Action<ILogger, string, Exception?> MissingUnderlying =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(8101, nameof(MissingUnderlying)),
            "Self-improvement research skipped for {Market}: no underlying instrument.");
    private static readonly Action<ILogger, string, string, Exception?> ResearchStored =
        LoggerMessage.Define<string, string>(LogLevel.Information,
            new EventId(8102, nameof(ResearchStored)),
            "Daily self-improvement research stored for {Market}: {Verdict}.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RunIfDueAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15), timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await RunIfDueAsync(stoppingToken);
    }

    internal async Task RunIfDueAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var local = TimeZoneInfo.ConvertTime(now, India);
        if (local.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ||
            TimeOnly.FromDateTime(local.DateTime) < new TimeOnly(15, 35)) return;

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        foreach (var market in new[] { "nifty", "sensex" })
        {
            var date = DateOnly.FromDateTime(local.Date);
            var dayStart = ToUtc(date);
            var dayEnd = ToUtc(date.AddDays(1));
            if (await db.MarketStrategyAudits.AsNoTracking().AnyAsync(value =>
                    value.Market == market && value.Outcome == AuditOutcome &&
                    value.CandleTimeUtc >= dayStart && value.CandleTimeUtc < dayEnd,
                    cancellationToken)) continue;

            var report = await BuildAsync(db, market, date, now, cancellationToken);
            var underlyingId = await ResolveUnderlyingIdAsync(db, market, cancellationToken);
            if (underlyingId == Guid.Empty)
            {
                MissingUnderlying(logger, market, null);
                continue;
            }
            db.MarketStrategyAudits.Add(new(Guid.NewGuid(), market, underlyingId, now,
                AuditOutcome, report.ActualWinRate / 100m,
                JsonSerializer.Serialize(report)));
            await db.SaveChangesAsync(cancellationToken);
            ResearchStored(logger, market, report.Verdict, null);
        }
    }

    private static async Task<SelfImprovementResearchReport> BuildAsync(TradingDbContext db,
        string market, DateOnly sessionDate, DateTimeOffset now, CancellationToken token)
    {
        var cutoff = ToUtc(sessionDate.AddDays(-45));
        var trades = new List<ResearchTradeObservation>();
        var decisions = new List<ResearchDecisionObservation>();
        if (market == "nifty")
        {
            var rows = await (from result in db.PaperTradeResults.AsNoTracking()
                join signal in db.Signals.AsNoTracking() on result.SignalId equals signal.Id
                join version in db.StrategyVersions.AsNoTracking() on signal.StrategyVersionId equals version.Id
                join strategy in db.Strategies.AsNoTracking() on version.StrategyId equals strategy.Id
                where result.ClosedAtUtc >= cutoff
                select new { result.ClosedAtUtc, result.RealisedPnl, result.EstimatedCosts,
                    Strategy = strategy.Code }).ToListAsync(token);
            trades.AddRange(rows.Select(value => new ResearchTradeObservation(LocalDate(value.ClosedAtUtc),
                value.Strategy, TimeBucket(value.ClosedAtUtc), value.RealisedPnl, value.EstimatedCosts)));
            var evaluations = await db.StrategyEvaluations.AsNoTracking()
                .Where(value => value.RecordedAtUtc >= cutoff)
                .Select(value => new { value.RecordedAtUtc, value.Outcome }).ToListAsync(token);
            decisions.AddRange(evaluations.Select(value =>
                new ResearchDecisionObservation(LocalDate(value.RecordedAtUtc), value.Outcome)));
        }
        else
        {
            var rows = await db.MarketPaperPositions.AsNoTracking()
                .Where(value => value.Market == market && value.ClosedAtUtc != null &&
                                value.ClosedAtUtc >= cutoff)
                .Select(value => new { value.ClosedAtUtc, value.Strategy, value.Direction,
                    value.EntryPrice, value.CurrentPrice, value.Quantity, value.RealisedPnl })
                .ToListAsync(token);
            trades.AddRange(rows.Select(value =>
            {
                var gross = (value.Direction == Direction.Buy ? value.CurrentPrice - value.EntryPrice :
                    value.EntryPrice - value.CurrentPrice) * value.Quantity;
                return new ResearchTradeObservation(LocalDate(value.ClosedAtUtc!.Value), value.Strategy,
                    TimeBucket(value.ClosedAtUtc.Value), value.RealisedPnl,
                    Math.Max(0m, gross - value.RealisedPnl));
            }));
            var audits = await db.MarketStrategyAudits.AsNoTracking()
                .Where(value => value.Market == market && value.CandleTimeUtc >= cutoff &&
                                value.Outcome != AuditOutcome)
                .Select(value => new { value.CandleTimeUtc, value.Outcome }).ToListAsync(token);
            decisions.AddRange(audits.Select(value =>
                new ResearchDecisionObservation(LocalDate(value.CandleTimeUtc), value.Outcome)));
        }
        return SelfImprovementResearchAnalyzer.Analyze(market, sessionDate, trades, decisions, now);
    }

    private static async Task<Guid> ResolveUnderlyingIdAsync(TradingDbContext db, string market,
        CancellationToken token)
    {
        var recent = await db.MarketStrategyAudits.AsNoTracking()
            .Where(value => value.Market == market)
            .OrderByDescending(value => value.CandleTimeUtc)
            .Select(value => value.UnderlyingInstrumentId).FirstOrDefaultAsync(token);
        if (recent != Guid.Empty) return recent;
        var symbol = market == "nifty" ? "NIFTY" : "SENSEX";
        return await db.Instruments.AsNoTracking().Where(value => value.TradingSymbol == symbol)
            .Select(value => value.Id).FirstOrDefaultAsync(token);
    }

    private static DateTimeOffset ToUtc(DateOnly date)
    {
        var local = date.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, India.GetUtcOffset(local)).ToUniversalTime();
    }
    private static DateOnly LocalDate(DateTimeOffset value) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, India).Date);
    private static string TimeBucket(DateTimeOffset value)
    {
        var hour = TimeZoneInfo.ConvertTime(value, India).Hour;
        return hour < 11 ? "Opening" : hour < 14 ? "Midday" : "Late";
    }
    private static TimeZoneInfo FindIndiaTimeZone()
    {
        foreach (var id in new[] { "India Standard Time", "Asia/Kolkata" })
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch (TimeZoneNotFoundException) { }
        return TimeZoneInfo.CreateCustomTimeZone("IST", TimeSpan.FromHours(5.5), "IST", "IST");
    }
}

internal sealed class EfSelfImprovementResearchReader(TradingDbContext db)
    : ISelfImprovementResearchReader
{
    public async Task<IReadOnlyList<SelfImprovementResearchReport>> GetLatestAsync(
        CancellationToken cancellationToken)
    {
        var rows = await db.MarketStrategyAudits.AsNoTracking()
            .Where(value => value.Outcome == SelfImprovementResearchService.AuditOutcome &&
                            (value.Market == "nifty" || value.Market == "sensex"))
            .OrderByDescending(value => value.CandleTimeUtc).Take(30)
            .Select(value => new { value.Market, value.ReasonsJson }).ToListAsync(cancellationToken);
        return rows.GroupBy(value => value.Market).Select(group => group.First())
            .Select(value => JsonSerializer.Deserialize<SelfImprovementResearchReport>(value.ReasonsJson))
            .Where(value => value is not null).Cast<SelfImprovementResearchReport>().ToArray();
    }
}
