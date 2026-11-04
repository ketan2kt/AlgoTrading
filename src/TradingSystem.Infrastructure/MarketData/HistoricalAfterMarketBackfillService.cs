using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingSystem.Application.Broker;
using TradingSystem.Application.Execution;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.Infrastructure.MarketData;

// Recover only missing index/confirmation-future candles. Five small series,
// not another daytime quote poller or a new trading strategy.
internal sealed class HistoricalAfterMarketBackfillService(IServiceScopeFactory scopes,
    IGrowwReadOnlyGateway gateway, IOptions<HistoricalFeedCaptureOptions> options,
    TimeProvider clock, ILogger<HistoricalAfterMarketBackfillService> logger) : BackgroundService
{
    private static readonly string[] Markets = ["NIFTY", "SENSEX", "BANKNIFTY"];
    private static readonly Action<ILogger, string, Exception?> Failed = LoggerMessage.Define<string>(
        LogLevel.Warning, new EventId(6754, "HistoricalAfterMarketBackfillFailed"),
        "After-market research backfill failed ({FailureType}); existing trading candles were not changed.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.AfterMarketBackfillEnabled) return;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), clock);
        do
        {
            if (DueDate(clock.GetUtcNow()) is not { } date) continue;
            try { await RunOnceAsync(date, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception) { Failed(logger, exception.GetType().Name, null); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal static DateOnly? DueDate(DateTimeOffset now)
    {
        var local = now.ToOffset(TimeSpan.FromHours(5.5));
        var time = TimeOnly.FromDateTime(local.DateTime);
        if (time >= new TimeOnly(8, 45) && time < new TimeOnly(16, 0)) return null;
        var date = DateOnly.FromDateTime(local.DateTime);
        if (time < new TimeOnly(8, 45) || date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) date = date.AddDays(-1);
        while (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) date = date.AddDays(-1);
        return date;
    }

    private async Task RunOnceAsync(DateOnly date, CancellationToken token)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var importer = scope.ServiceProvider.GetRequiredService<GrowwHistoricalResearchImporter>();
        var start = new DateTimeOffset(date.ToDateTime(new TimeOnly(9, 15)), TimeSpan.FromHours(5.5)).ToUniversalTime();
        var end = start.AddMinutes(375);
        foreach (var market in Markets)
        {
            var exchange = market == "SENSEX" ? "BSE" : "NSE";
            var rows = await db.Instruments.AsNoTracking().Where(value => value.Exchange == exchange &&
                value.TradingSymbol.StartsWith(market) && (value.Type == InstrumentType.Index ||
                    value.Type == InstrumentType.Future && value.ExpiryDate >= date))
                .OrderBy(value => value.ExpiryDate).ToArrayAsync(token);
            var index = rows.SingleOrDefault(value => value.Type == InstrumentType.Index && value.TradingSymbol == market);
            var future = market == "BANKNIFTY" ? null : rows.FirstOrDefault(value => value.Type == InstrumentType.Future);
            foreach (var instrument in new[] { index, future }.OfType<Instrument>())
            {
                if (string.IsNullOrWhiteSpace(instrument.GrowwSymbol)) continue;
                var key = $"ReplayEod:{date.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}:{instrument.Id:N}";
                if (await db.SystemEvents.AsNoTracking().AnyAsync(value => value.EventType == key + ":Complete", token)) continue;
                var failures = await db.SystemEvents.AsNoTracking().Where(value => value.EventType == key + ":Failed")
                    .OrderByDescending(value => value.OccurredAtUtc).Select(value => value.OccurredAtUtc).ToArrayAsync(token);
                var backoff = TimeSpan.FromMinutes(Math.Min(120, 5 * Math.Pow(3, Math.Min(4, failures.Length))));
                if (failures.Length > 0 && clock.GetUtcNow() - failures[0] < backoff) continue;
                try
                {
                    var recorded = await db.Candles.AsNoTracking().Where(value => value.InstrumentId == instrument.Id &&
                        value.Source == "Groww" && value.IntervalSeconds == 60 && value.OpenTimeUtc >= start && value.OpenTimeUtc < end)
                        .Select(value => value.OpenTimeUtc).ToArrayAsync(token);
                    var repairs = await db.HistoricalResearchCandles.AsNoTracking().Where(value => value.GrowwSymbol == instrument.GrowwSymbol &&
                        value.IntervalSeconds == 60 && value.OpenTimeUtc >= start && value.OpenTimeUtc < end)
                        .Select(value => value.OpenTimeUtc).ToArrayAsync(token);
                    var coverage = HistoricalSessionCoverageEvaluator.Evaluate(date, recorded.Concat(repairs));
                    if (!coverage.IsComplete)
                    {
                        var history = await gateway.GetHistoricalCandlesAsync(new(exchange,
                            instrument.Type == InstrumentType.Index ? "CASH" : "FNO", instrument.GrowwSymbol,
                            start, end, "1minute"), token);
                        await importer.ImportMappedAsync(instrument, market, date, history, start, end, clock.GetUtcNow(), token);
                        coverage = HistoricalSessionCoverageEvaluator.Evaluate(date, recorded.Concat(repairs).Concat(history.Candles.Select(value =>
                            GrowwHistoricalCandleImporter.ParseIndiaTimestamp(value.SourceTimestamp))));
                    }
                    db.SystemEvents.Add(new(Guid.NewGuid(), coverage.IsComplete ? "Information" : "Warning",
                        key + (coverage.IsComplete ? ":Complete" : ":Failed"), "Research-only after-market candle coverage check.", clock.GetUtcNow(),
                        JsonSerializer.Serialize(new { date, market, instrument.TradingSymbol, coverage.PresentMinutes, coverage.MissingMinutes })));
                    await db.SaveChangesAsync(token);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    db.ChangeTracker.Clear();
                    db.SystemEvents.Add(new(Guid.NewGuid(), "Warning", key + ":Failed",
                        "After-market research import failed; it will retry with backoff.", clock.GetUtcNow(),
                        JsonSerializer.Serialize(new { date, market, failureType = exception.GetType().Name })));
                    await db.SaveChangesAsync(token);
                    Failed(logger, exception.GetType().Name, null);
                }
            }
        }
    }
}
