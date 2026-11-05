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

internal sealed partial class GrowwAdditionalMarketDataService(
    IServiceScopeFactory scopeFactory,
    IGrowwReadOnlyGateway gateway,
    GrowwQuoteNormalizer normalizer,
    MultiMarketFeedState feedState,
    MarketWorkspaceRefreshQueue refreshQueue,
    IOptions<LiveNiftyOptions> options,
    TimeProvider timeProvider,
    ILogger<GrowwAdditionalMarketDataService> logger) : BackgroundService
{
    private static readonly TimeZoneInfo IndiaTimeZone = FindIndiaTimeZone();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        // Separate lifetimes and DbContexts: one index/history request cannot block another index loop.
        await Task.WhenAll(
            RunMarketAsync(TradingMarketCatalog.Sensex, stoppingToken),
            RunMarketAsync(TradingMarketCatalog.BankNifty, stoppingToken),
            BootstrapHistoryAsync(TradingMarketCatalog.Sensex, stoppingToken),
            BootstrapHistoryAsync(TradingMarketCatalog.BankNifty, stoppingToken));
    }

    private async Task RunMarketAsync(TradingMarketDefinition market, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (IsMarketWindow(market, timeProvider.GetUtcNow()))
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    timeout.CancelAfter(TimeSpan.FromSeconds(15));
                    await PollAsync(market, timeout.Token);
                }
                else
                    feedState.RecordStatus(market.Code, "MarketClosed", $"Waiting for the {market.Exchange} session.");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (OperationCanceledException)
            {
                feedState.RecordStatus(market.Code, "Disconnected", "Quote ingestion exceeded 15 seconds; retrying automatically.");
            }
            catch (Exception exception)
            {
                var detail = exception is GrowwApiException { StatusCode: not null } api
                    ? FormattableString.Invariant($"Groww HTTP {api.StatusCode}; quote polling will retry.")
                    : "Quote ingestion failed; awaiting recovery.";
                feedState.RecordStatus(market.Code, "Disconnected", detail);
                LogPollingFailed(logger, market.Code, exception);
            }
            refreshQueue.Request(market.Code);
            // Six-second Bank Nifty sleep exceeded the five-second entry freshness window.
            // Use the same cadence as Nifty/Sensex; the shared gateway still enforces API limits.
            var seconds = IsMarketWindow(market, timeProvider.GetUtcNow()) ? options.Value.PollIntervalSeconds : 30;
            await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken);
        }
    }

    private async Task PollAsync(TradingMarketDefinition market, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        feedState.RecordStage(market.Code, "Resolving the active index instrument.");
        var now = timeProvider.GetUtcNow();
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, IndiaTimeZone).Date);
        var query = db.Instruments.Where(value => value.Exchange == market.Exchange &&
                                                   value.Type == market.InstrumentType && value.IsActive);
        var instrument = market.InstrumentType == InstrumentType.Index
            ? await query.SingleOrDefaultAsync(value => value.TradingSymbol == market.UnderlyingSymbol,
                cancellationToken)
            : await query.Where(value => value.TradingSymbol.StartsWith(market.UnderlyingSymbol) &&
                                         value.ExpiryDate >= today)
                .OrderBy(value => value.ExpiryDate).ThenBy(value => value.TradingSymbol)
                .FirstOrDefaultAsync(cancellationToken);
        if (instrument is null)
        {
            feedState.RecordStatus(market.Code, "InstrumentUnavailable", "Run Groww instrument synchronisation.");
            return;
        }

        feedState.RecordStage(market.Code, "Waiting for Groww quote / rate-limit queue.");
        var quote = await gateway.GetQuoteAsync(new(market.Exchange, market.QuoteSegment,
            instrument.TradingSymbol), cancellationToken);
        var observation = normalizer.Normalize(instrument.Id, quote, timeProvider.GetUtcNow());
        feedState.RecordStage(market.Code, "Validating and persisting the received quote.");
        var processed = await scope.ServiceProvider.GetRequiredService<MarketDataProcessor>()
            .ProcessAsync(observation, cancellationToken);
        if (processed.Validation.Accepted)
            feedState.RecordSuccess(market.Code, observation.SourceTimestampUtc, observation.ReceivedAtUtc);
        else feedState.RecordStage(market.Code, $"Latest quote rejected: {processed.Validation.Reason}; awaiting a valid observation.");
    }

    private async Task BootstrapHistoryAsync(TradingMarketDefinition market, CancellationToken stoppingToken)
    {
        DateOnly? completedSession = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = timeProvider.GetUtcNow();
                var local = TimeZoneInfo.ConvertTime(now, IndiaTimeZone);
                var today = DateOnly.FromDateTime(local.Date);
                // Preload before the open when possible; retry failures without stalling live quotes.
                if (local.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) &&
                    TimeOnly.FromDateTime(local.DateTime) >= new TimeOnly(9, 5) &&
                    TimeOnly.FromDateTime(local.DateTime) <= market.SessionEnd && completedSession != today)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    timeout.CancelAfter(TimeSpan.FromSeconds(60));
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
                    var instrument = await db.Instruments.AsNoTracking().SingleOrDefaultAsync(value =>
                        value.Exchange == market.Exchange && value.Type == market.InstrumentType &&
                        value.IsActive && value.TradingSymbol == market.UnderlyingSymbol, timeout.Token);
                    if (instrument?.GrowwSymbol is not null)
                    {
                        var start = new DateTimeOffset(local.Date.AddDays(-7).AddHours(9).AddMinutes(15), local.Offset).ToUniversalTime();
                        var history = await gateway.GetHistoricalCandlesAsync(new(market.Exchange, market.QuoteSegment,
                            instrument.GrowwSymbol, start, now, "1minute"), timeout.Token);
                        await scope.ServiceProvider.GetRequiredService<GrowwHistoricalCandleImporter>()
                            .ImportAsync(instrument, history, now, timeout.Token);
                        completedSession = today;
                        refreshQueue.Request(market.Code);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception) { LogHistoryFailed(logger, market.Code, exception); }
            await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
        }
    }

    private static bool IsMarketWindow(TradingMarketDefinition market, DateTimeOffset now)
    {
        var india = TimeZoneInfo.ConvertTime(now, IndiaTimeZone);
        if (india.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return false;
        var time = TimeOnly.FromDateTime(india.DateTime);
        return time >= market.SessionStart && time <= market.SessionEnd;
    }

    private static TimeZoneInfo FindIndiaTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("India Standard Time"); }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Groww {Market} polling failed.")]
    private static partial void LogPollingFailed(ILogger logger, string market, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Groww {Market} history bootstrap failed; live quote polling remains independent.")]
    private static partial void LogHistoryFailed(ILogger logger, string market, Exception exception);
}
