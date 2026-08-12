using Microsoft.EntityFrameworkCore;
using TradingSystem.Application.Execution;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.Infrastructure.Execution;

internal sealed class EfPaperTradingReportReader(TradingDbContext db, TimeProvider timeProvider)
    : IPaperTradingReportReader
{
    private static readonly TimeZoneInfo India = FindIndiaTimeZone();

    public async Task<PaperTradingReport> GetAsync(int days, CancellationToken cancellationToken)
    {
        days = Math.Clamp(days, 1, 90);
        var cutoff = timeProvider.GetUtcNow().AddDays(-days - 2);
        var rows = await (from result in db.PaperTradeResults.AsNoTracking()
            join signal in db.Signals.AsNoTracking() on result.SignalId equals signal.Id
            where result.ClosedAtUtc >= cutoff
            orderby result.ClosedAtUtc descending
            select new PaperTradeHistoryItem(result.SignalId, result.TradingSymbol,
                signal.Direction.ToString(), result.Quantity, result.EntryPrice, result.ExitPrice,
                result.RealisedPnl, result.ExitReason, signal.MarketDataTimestampUtc,
                result.ClosedAtUtc, "Opening Range Breakout 1.0.0"))
            .Take(500).ToListAsync(cancellationToken);
        var daily = rows.GroupBy(value => DateOnly.FromDateTime(
                TimeZoneInfo.ConvertTime(value.ExitTimeUtc, India).Date))
            .OrderByDescending(group => group.Key).Take(days)
            .Select(group => Summarise(group.Key, group.OrderBy(value => value.ExitTimeUtc)))
            .ToArray();
        return new(daily, rows, timeProvider.GetUtcNow());
    }

    private static DailyPaperPerformance Summarise(DateOnly date,
        IEnumerable<PaperTradeHistoryItem> trades)
    {
        var values = trades.ToArray();
        var peak = 0m; var equity = 0m; var drawdown = 0m;
        foreach (var trade in values)
        {
            equity += trade.RealisedPnl;
            peak = Math.Max(peak, equity);
            drawdown = Math.Max(drawdown, peak - equity);
        }
        var wins = values.Count(value => value.RealisedPnl > 0);
        return new(date, values.Length, wins, values.Count(value => value.RealisedPnl <= 0),
            values.Sum(value => value.RealisedPnl),
            values.Where(value => value.RealisedPnl > 0).Sum(value => value.RealisedPnl),
            values.Where(value => value.RealisedPnl < 0).Sum(value => value.RealisedPnl),
            values.Length == 0 ? 0m : (decimal)wins / values.Length, drawdown);
    }

    private static TimeZoneInfo FindIndiaTimeZone()
    {
        foreach (var id in new[] { "Asia/Kolkata", "India Standard Time" })
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch (TimeZoneNotFoundException) { }
        throw new TimeZoneNotFoundException("India timezone unavailable.");
    }
}
