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
    IOptions<LiveNiftyOptions> options,
    TimeProvider timeProvider,
    ILogger<GrowwAdditionalMarketDataService> logger) : BackgroundService
{
    private static readonly TimeZoneInfo IndiaTimeZone = FindIndiaTimeZone();
    private readonly Dictionary<string, DateOnly> bootstrappedSessions = new(StringComparer.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var market in new[] { TradingMarketCatalog.Sensex, TradingMarketCatalog.NaturalGas })
            {
                try
                {
                    if (IsMarketWindow(market, timeProvider.GetUtcNow()))
                        await PollAsync(market, stoppingToken);
                    else
                        feedState.RecordStatus(market.Code, "MarketClosed", $"Waiting for the {market.Exchange} session.");
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception exception)
                {
                    feedState.RecordStatus(market.Code, "Disconnected", "Groww quote polling failed.");
                    LogPollingFailed(logger, market.Code, exception);
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(options.Value.PollIntervalSeconds), stoppingToken);
        }
    }

    private async Task PollAsync(TradingMarketDefinition market, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
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

        if (market == TradingMarketCatalog.Sensex && instrument.GrowwSymbol is not null &&
            bootstrappedSessions.GetValueOrDefault(market.Code) != today)
        {
            var localStart = TimeZoneInfo.ConvertTime(now, IndiaTimeZone).Date.AddDays(-7).AddHours(9).AddMinutes(15);
            var start = new DateTimeOffset(localStart, IndiaTimeZone.GetUtcOffset(localStart)).ToUniversalTime();
            var history = await gateway.GetHistoricalCandlesAsync(new(market.Exchange, market.QuoteSegment,
                instrument.GrowwSymbol, start, now, "1minute"), cancellationToken);
            await scope.ServiceProvider.GetRequiredService<GrowwHistoricalCandleImporter>()
                .ImportAsync(instrument, history, now, cancellationToken);
            bootstrappedSessions[market.Code] = today;
        }

        var quote = await gateway.GetQuoteAsync(new(market.Exchange, market.QuoteSegment,
            instrument.TradingSymbol), cancellationToken);
        var observation = normalizer.Normalize(instrument.Id, quote, now);
        var processed = await scope.ServiceProvider.GetRequiredService<MarketDataProcessor>()
            .ProcessAsync(observation, cancellationToken);
        if (processed.Validation.Accepted)
            feedState.RecordSuccess(market.Code, observation.SourceTimestampUtc, observation.ReceivedAtUtc);
        var snapshot = await scope.ServiceProvider.GetRequiredService<ITradingWorkspaceReader>()
            .GetAsync(market.Code, options.Value.WorkspaceCandleCount, cancellationToken);
        await scope.ServiceProvider.GetRequiredService<ILiveMarketDataPublisher>()
            .PublishAsync(market.Code, snapshot, cancellationToken);
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
}
