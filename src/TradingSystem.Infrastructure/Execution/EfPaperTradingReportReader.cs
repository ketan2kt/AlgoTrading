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
        var baseRows = await (from result in db.PaperTradeResults.AsNoTracking()
            join signal in db.Signals.AsNoTracking() on result.SignalId equals signal.Id
            join version in db.StrategyVersions.AsNoTracking() on signal.StrategyVersionId equals version.Id
            join strategy in db.Strategies.AsNoTracking() on version.StrategyId equals strategy.Id
            join instrument in db.Instruments.AsNoTracking() on result.InstrumentId equals instrument.Id
            where result.ClosedAtUtc >= cutoff
            orderby result.ClosedAtUtc descending
            select new { Result = result, Signal = signal, Strategy = strategy.Code,
                instrument.ExpiryDate })
            .Take(500).ToListAsync(cancellationToken);
        var signalIds = baseRows.Select(x => x.Signal.Id).ToArray();
        var regimeRows = await db.StrategyEvaluations.AsNoTracking()
            .Where(x => x.SignalId != null && signalIds.Contains(x.SignalId.Value))
            .Select(x => new { SignalId = x.SignalId!.Value, x.Regime, x.RecordedAtUtc })
            .ToListAsync(cancellationToken);
        var regimes = regimeRows.GroupBy(x => x.SignalId)
            .ToDictionary(group => group.Key,
                group => group.OrderByDescending(x => x.RecordedAtUtc).First().Regime.ToString());
        var rows = baseRows.Select(x => new PaperTradeHistoryItem(x.Result.SignalId, x.Result.TradingSymbol,
                x.Signal.Direction.ToString(), x.Result.Quantity, x.Result.EntryPrice, x.Result.ExitPrice,
                x.Result.RealisedPnl, x.Result.ExitReason, x.Signal.MarketDataTimestampUtc,
                x.Result.ClosedAtUtc, x.Strategy, regimes.GetValueOrDefault(x.Signal.Id, "Unknown"),
                x.ExpiryDate is { } expiry ? Math.Max(0, expiry.DayNumber - DateOnly.FromDateTime(
                    TimeZoneInfo.ConvertTime(x.Signal.MarketDataTimestampUtc, India).Date).DayNumber) : -1))
            .ToList();
        var daily = rows.GroupBy(value => DateOnly.FromDateTime(
                TimeZoneInfo.ConvertTime(value.ExitTimeUtc, India).Date))
            .OrderByDescending(group => group.Key).Take(days)
            .Select(group => Summarise(group.Key, group.OrderBy(value => value.ExitTimeUtc)))
            .ToArray();
        var breakdown = rows.GroupBy(value => new
            {
                value.Strategy,
                value.Regime,
                TimeBucket = TimeBucket(value.SignalTimeUtc),
                value.DaysToExpiry
            })
            .OrderByDescending(group => group.Count())
            .Select(group => new StrategyPerformanceBreakdown(group.Key.Strategy, group.Key.Regime,
                group.Key.TimeBucket, group.Key.DaysToExpiry, group.Count(),
                group.Count(x => x.RealisedPnl > 0), group.Sum(x => x.RealisedPnl),
                (decimal)group.Count(x => x.RealisedPnl > 0) / group.Count(),
                group.Average(x => x.RealisedPnl)))
            .ToArray();
        return new(daily, rows, breakdown, timeProvider.GetUtcNow());
    }

    private static string TimeBucket(DateTimeOffset utc)
    {
        var time = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, India).DateTime);
        return time < new TimeOnly(11, 0) ? "09:15-11:00"
            : time < new TimeOnly(13, 0) ? "11:00-13:00" : "13:00-cutoff";
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
