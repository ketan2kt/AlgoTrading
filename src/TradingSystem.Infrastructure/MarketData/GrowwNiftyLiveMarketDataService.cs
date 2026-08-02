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
    IOptions<LiveNiftyOptions> options,
    IOptions<GrowwOptions> growwOptions,
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

        await RestoreProjectionAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeSpan.FromSeconds(options.Value.PollIntervalSeconds);
            try
            {
                if (!await HasAccessTokenAsync(stoppingToken))
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
                    await PollAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                feedState.RecordStatus("Disconnected", "Groww live quote polling failed; trading remains blocked.");
                LogPollingFailed(logger, exception);
                delay = TimeSpan.FromSeconds(Math.Max(5, options.Value.PollIntervalSeconds));
            }

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
        await using var scope = scopeFactory.CreateAsyncScope();
        var vault = scope.ServiceProvider.GetRequiredService<IGrowwTokenVault>();
        if (await vault.GetValidTokenAsync(cancellationToken) is not null)
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
            growwOptions.Value.AccessTokenEnvironmentVariable));
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var instrument = await dbContext.Instruments.AsNoTracking()
            .SingleOrDefaultAsync(value => value.Exchange == options.Value.Exchange &&
                                           value.TradingSymbol == options.Value.TradingSymbol &&
                                           value.Segment == InstrumentSegment.Cash &&
                                           value.IsActive,
                cancellationToken) ?? throw new InvalidOperationException(
                    "The active NSE NIFTY cash instrument is not synchronised.");
        var receivedAt = timeProvider.GetUtcNow();
        var quote = await gateway.GetQuoteAsync(new(
            options.Value.Exchange,
            options.Value.Segment,
            options.Value.TradingSymbol), cancellationToken);
        var observation = normalizer.Normalize(instrument.Id, quote, receivedAt);
        var processor = scope.ServiceProvider.GetRequiredService<MarketDataProcessor>();
        var result = await processor.ProcessAsync(observation, cancellationToken);
        if (result.Validation.Accepted)
        {
            feedState.RecordSuccess(observation.SourceTimestampUtc, observation.ReceivedAtUtc);
        }

        var reader = scope.ServiceProvider.GetRequiredService<ITradingWorkspaceReader>();
        var snapshot = await reader.GetNiftyAsync(options.Value.WorkspaceCandleCount, cancellationToken);
        var publisher = scope.ServiceProvider.GetRequiredService<ILiveMarketDataPublisher>();
        await publisher.PublishNiftyAsync(snapshot, cancellationToken);
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
}
