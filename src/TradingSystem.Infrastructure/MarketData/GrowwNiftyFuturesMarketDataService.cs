using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingSystem.Application.Broker;
using TradingSystem.Application.MarketData;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.Infrastructure.MarketData;

internal sealed partial class GrowwNiftyFuturesMarketDataService(
    IServiceScopeFactory scopeFactory,
    IGrowwReadOnlyGateway gateway,
    GrowwQuoteNormalizer normalizer,
    IOptions<LiveNiftyOptions> options,
    FuturesFeedHealthMonitor feedHealth,
    TimeProvider timeProvider,
    ILogger<GrowwNiftyFuturesMarketDataService> logger) : BackgroundService
{
    private static readonly TimeZoneInfo IndiaTimeZone = FindIndiaTimeZone();
    private DateOnly? bootstrappedSession;
    private DateTimeOffset? lastFuturesHistoryRefreshUtc;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeSpan.FromSeconds(options.Value.PollIntervalSeconds);
            try
            {
                var now = timeProvider.GetUtcNow();
                if (IsMarketWindow(now)) await ProcessAsync(now, stoppingToken);
                else delay = TimeSpan.FromSeconds(30);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            { break; }
            catch (Exception exception)
            {
                var errorCode = exception is GrowwApiException growwException
                    ? growwException.ErrorCode ?? "GROWW_API_ERROR"
                    : "INTERNAL_FEED_ERROR";
                var errorDetail = exception is GrowwApiException
                    ? exception.Message
                    : "The futures feed encountered an internal processing error.";
                feedHealth.RecordFailure(timeProvider.GetUtcNow(), errorCode, errorDetail);
                LogPollingFailed(logger, exception);
                delay = TimeSpan.FromSeconds(10);
            }
            await Task.Delay(delay, stoppingToken);
        }
    }

    private async Task ProcessAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var indiaNow = TimeZoneInfo.ConvertTime(now, IndiaTimeZone);
        var session = DateOnly.FromDateTime(indiaNow.Date);
        var index = await db.Instruments.SingleOrDefaultAsync(value =>
            value.Exchange == "NSE" && value.Type == InstrumentType.Index &&
            value.TradingSymbol == "NIFTY" && value.IsActive, cancellationToken);
        var future = await db.Instruments.Where(value => value.Exchange == "NSE" &&
                value.Type == InstrumentType.Future && value.TradingSymbol.StartsWith("NIFTY") &&
                value.ExpiryDate >= session && value.IsActive)
            .OrderBy(value => value.ExpiryDate).ThenBy(value => value.TradingSymbol)
            .FirstOrDefaultAsync(cancellationToken);
        if (index?.GrowwSymbol is null || future?.GrowwSymbol is null) return;

        var sessionStartIndia = indiaNow.Date.AddHours(9).AddMinutes(15);
        var sessionStart = new DateTimeOffset(sessionStartIndia,
            IndiaTimeZone.GetUtcOffset(sessionStartIndia)).ToUniversalTime();
        var historyRefreshed = false;
        if (now > sessionStart &&
            (lastFuturesHistoryRefreshUtc is null ||
             now - lastFuturesHistoryRefreshUtc >= TimeSpan.FromSeconds(30)))
        {
            var importer = scope.ServiceProvider.GetRequiredService<GrowwHistoricalCandleImporter>();
            var futureHistory = await gateway.GetHistoricalCandlesAsync(new("NSE", "FNO",
                future.GrowwSymbol, sessionStart, now, "1minute"), cancellationToken);
            await importer.ImportAsync(future, futureHistory, now, cancellationToken);
            lastFuturesHistoryRefreshUtc = now;
            historyRefreshed = true;

            if (bootstrappedSession != session)
            {
                var startIndia = indiaNow.Date.AddDays(-7).AddHours(9).AddMinutes(15);
                var start = new DateTimeOffset(startIndia,
                    IndiaTimeZone.GetUtcOffset(startIndia)).ToUniversalTime();
                var indexHistory = await gateway.GetHistoricalCandlesAsync(new("NSE", "CASH",
                    index.GrowwSymbol, start, now, "1minute"), cancellationToken);
                await importer.ImportAsync(index, indexHistory, now, cancellationToken);
                bootstrappedSession = session;
            }
        }

        var quote = await gateway.GetQuoteAsync(new("NSE", "FNO", future.TradingSymbol),
            cancellationToken);
        var observation = normalizer.Normalize(future.Id, quote, now);
        var processor = scope.ServiceProvider.GetRequiredService<MarketDataProcessor>();
        var result = await processor.ProcessAsync(observation, cancellationToken);
        if (historyRefreshed || result.Validation.Accepted)
            feedHealth.RecordSuccess(now);
    }

    private static bool IsMarketWindow(DateTimeOffset now)
    {
        var india = TimeZoneInfo.ConvertTime(now, IndiaTimeZone);
        if (india.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return false;
        var time = TimeOnly.FromDateTime(india.DateTime);
        return time >= new TimeOnly(9, 15) && time <= new TimeOnly(15, 30);
    }

    private static TimeZoneInfo FindIndiaTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"); }
        catch (TimeZoneNotFoundException)
        { return TimeZoneInfo.FindSystemTimeZoneById("India Standard Time"); }
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Groww Nifty futures confirmation feed is not ready; paper entries remain blocked.")]
    private static partial void LogPollingFailed(ILogger logger, Exception exception);
}
