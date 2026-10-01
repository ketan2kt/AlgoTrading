using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingSystem.Application.Broker;
using TradingSystem.Application.MarketData;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Broker.Groww;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.Infrastructure.MarketData;

/// <summary>
/// Maintains the current Sensex futures candle stream used for volume confirmation and VWAP.
/// The cash index itself has no useful traded volume, so substituting its zero volume makes
/// every Sensex volume rule and VWAP audit misleading.
/// </summary>
internal sealed partial class GrowwSensexFuturesMarketDataService(
    IServiceScopeFactory scopeFactory,
    IGrowwReadOnlyGateway gateway,
    GrowwQuoteNormalizer normalizer,
    IOptions<LiveNiftyOptions> options,
    TimeProvider timeProvider,
    ILogger<GrowwSensexFuturesMarketDataService> logger) : BackgroundService
{
    private static readonly TimeZoneInfo IndiaTimeZone = FindIndiaTimeZone();
    private DateTimeOffset? lastHistoryRefreshUtc;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = timeProvider.GetUtcNow();
                if (IsMarketWindow(now)) await ProcessAsync(now, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception) { LogPollingFailed(logger, exception); }
            await Task.Delay(TimeSpan.FromSeconds(options.Value.PollIntervalSeconds), stoppingToken);
        }
    }

    private async Task ProcessAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var india = TimeZoneInfo.ConvertTime(now, IndiaTimeZone);
        var today = DateOnly.FromDateTime(india.Date);
        var future = await db.Instruments.Where(value => value.Exchange == "BSE" &&
                value.Type == InstrumentType.Future && value.TradingSymbol.StartsWith("SENSEX") &&
                value.ExpiryDate >= today && value.IsActive)
            .OrderBy(value => value.ExpiryDate).ThenBy(value => value.TradingSymbol)
            .FirstOrDefaultAsync(cancellationToken);
        if (future?.GrowwSymbol is null) return;

        var startIndia = india.Date.AddHours(9).AddMinutes(15);
        var start = new DateTimeOffset(startIndia,
            IndiaTimeZone.GetUtcOffset(startIndia)).ToUniversalTime();
        if (lastHistoryRefreshUtc is null || now - lastHistoryRefreshUtc >= TimeSpan.FromSeconds(30))
        {
            var history = await gateway.GetHistoricalCandlesAsync(new("BSE", "FNO",
                future.GrowwSymbol, start, now, "1minute"), cancellationToken);
            await scope.ServiceProvider.GetRequiredService<GrowwHistoricalCandleImporter>()
                .ImportAsync(future, history, now, cancellationToken);
            lastHistoryRefreshUtc = now;
        }

        var quote = await gateway.GetQuoteAsync(new("BSE", "FNO", future.TradingSymbol),
            cancellationToken);
        var observation = normalizer.Normalize(future.Id, quote, now);
        await scope.ServiceProvider.GetRequiredService<MarketDataProcessor>()
            .ProcessAsync(observation, cancellationToken);
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
        Message = "Groww Sensex futures confirmation polling failed.")]
    private static partial void LogPollingFailed(ILogger logger, Exception exception);
}
