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

internal sealed partial class GrowwNiftyLiveMarketDataService(
    IServiceScopeFactory scopeFactory,
    IGrowwReadOnlyGateway gateway,
    GrowwQuoteNormalizer normalizer,
    MarketDataValidator validator,
    CandleAggregator aggregator,
    LiveNiftyFeedState feedState,
    MarketWorkspaceRefreshQueue refreshQueue,
    IGrowwAccessTokenProvider accessTokenProvider,
    IOptions<LiveNiftyOptions> options,
    TimeProvider timeProvider,
    ILogger<GrowwNiftyLiveMarketDataService> logger) : BackgroundService
{
    private static readonly TimeZoneInfo IndiaTimeZone = FindIndiaTimeZone();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            feedState.RecordStatus("Disabled", "Live Nifty ingestion is disabled by server configuration.");
            return;
        }

        var restored = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeSpan.FromSeconds(options.Value.PollIntervalSeconds);
            try
            {
                using var cycleTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                cycleTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                if (!restored)
                {
                    feedState.RecordStatus("Starting", "Restoring persisted Nifty quote cursor.");
                    feedState.RecordStage("Restoring persisted Nifty quote cursor.");
                    await RestoreProjectionAsync(cycleTimeout.Token);
                    restored = true;
                }
                if (!await HasAccessTokenAsync(cycleTimeout.Token))
                {
                    feedState.RecordStatus("CredentialsRequired", "Groww access token is not configured on the server.");
                    delay = TimeSpan.FromSeconds(5);
                }
                else if (!IsMarketWindow(timeProvider.GetUtcNow()))
                {
                    feedState.RecordStatus("MarketClosed", "Waiting for the NSE cash-market session.");
                    delay = TimeSpan.FromSeconds(30);
                }
                else
                {
                    // A stalled quote or database write must not permanently
                    // stop the single Nifty ingestion loop while other market feeds continue.
                    using var pollTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    pollTimeout.CancelAfter(TimeSpan.FromSeconds(
                        Math.Max(15, options.Value.PollIntervalSeconds * 5)));
                    try
                    {
                        await PollAsync(pollTimeout.Token);
                    }
                    catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested &&
                                                              pollTimeout.IsCancellationRequested)
                    {
                        feedState.RecordStatus("Disconnected",
                            "Nifty polling exceeded its safety timeout; retrying automatically.");
                        LogPollingTimedOut(logger);
                        delay = TimeSpan.FromSeconds(Math.Max(5, options.Value.PollIntervalSeconds));
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                var detail = exception is GrowwApiException { StatusCode: not null } api
                    ? FormattableString.Invariant($"Groww HTTP {api.StatusCode}; quote polling will retry.")
                    : "Quote ingestion failed; trading remains blocked until recovery.";
                feedState.RecordStatus("Disconnected", detail);
                LogPollingFailed(logger, exception);
                delay = TimeSpan.FromSeconds(Math.Max(5, options.Value.PollIntervalSeconds));
            }
            refreshQueue.Request("nifty");
            await Task.Delay(delay, stoppingToken);
        }
    }

    private async Task RestoreProjectionAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var instrument = await dbContext.Instruments.AsNoTracking()
            .SingleOrDefaultAsync(value => value.Exchange == options.Value.Exchange &&
                                           value.TradingSymbol == options.Value.TradingSymbol &&
                                           value.Segment == InstrumentSegment.Cash &&
                                           value.IsActive,
                cancellationToken);
        if (instrument is null)
        {
            return;
        }

        var latest = await dbContext.MarketObservations.AsNoTracking()
            .Where(value => value.InstrumentId == instrument.Id && value.Source == "Groww")
            .OrderByDescending(value => value.SourceTimestampUtc)
            .FirstOrDefaultAsync(cancellationToken);
        if (latest is null)
        {
            return;
        }

        validator.RestoreCursor(instrument.Id, latest.Source, latest.SourceTimestampUtc);
        var interval = scope.ServiceProvider
            .GetRequiredService<IOptions<MarketDataOptions>>().Value.CandleIntervalSeconds;
        var bucket = DateTimeOffset.FromUnixTimeSeconds(
            latest.SourceTimestampUtc.ToUnixTimeSeconds() - latest.SourceTimestampUtc.ToUnixTimeSeconds() % interval);
        var observations = await dbContext.MarketObservations.AsNoTracking()
            .Where(value => value.InstrumentId == instrument.Id && value.Source == "Groww" &&
                            value.SourceTimestampUtc >= bucket)
            .OrderBy(value => value.SourceTimestampUtc)
            .ToListAsync(cancellationToken);
        foreach (var value in observations)
        {
            aggregator.Add(new MarketObservation(
                value.InstrumentId, value.Source, value.SourceTimestampUtc, value.ReceivedAtUtc,
                value.Price, value.VolumeDelta, value.OpenInterest));
        }
    }

    private async Task<bool> HasAccessTokenAsync(CancellationToken cancellationToken)
    {
        return !string.IsNullOrWhiteSpace(
            await accessTokenProvider.GetAccessTokenAsync(cancellationToken));
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        feedState.RecordStage("Resolving the active Nifty instrument.");
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var instrument = await dbContext.Instruments.AsNoTracking()
            .SingleOrDefaultAsync(value => value.Exchange == options.Value.Exchange &&
                                           value.TradingSymbol == options.Value.TradingSymbol &&
                                           value.Segment == InstrumentSegment.Cash &&
                                           value.IsActive,
                cancellationToken) ?? throw new InvalidOperationException(
                    "The active NSE NIFTY cash instrument is not synchronised.");
        feedState.RecordStage("Waiting for Groww quote / rate-limit queue.");
        var quote = await gateway.GetQuoteAsync(new(
            options.Value.Exchange,
            options.Value.Segment,
            options.Value.TradingSymbol), cancellationToken);
        var observation = normalizer.Normalize(instrument.Id, quote, timeProvider.GetUtcNow());
        feedState.RecordStage("Validating and persisting the received quote.");
        var processor = scope.ServiceProvider.GetRequiredService<MarketDataProcessor>();
        var result = await processor.ProcessAsync(observation, cancellationToken);
        if (result.Validation.Accepted)
        {
            feedState.RecordSuccess(observation.SourceTimestampUtc, observation.ReceivedAtUtc);
        }
        else feedState.RecordStage($"Latest quote rejected: {result.Validation.Reason}; awaiting a valid observation.");
    }

    private static bool IsMarketWindow(DateTimeOffset utcNow)
    {
        var india = TimeZoneInfo.ConvertTime(utcNow, IndiaTimeZone);
        if (india.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            return false;
        }

        var time = TimeOnly.FromDateTime(india.DateTime);
        return time >= new TimeOnly(9, 15) && time <= new TimeOnly(15, 30);
    }

    private static TimeZoneInfo FindIndiaTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Groww Nifty live quote polling failed.")]
    private static partial void LogPollingFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Groww Nifty live quote polling timed out; the next cycle will retry.")]
    private static partial void LogPollingTimedOut(ILogger logger);
}
