using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using TradingSystem.Application.Broker;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.Infrastructure.MarketData;

internal sealed partial class GrowwOptionResearchCaptureService(
    IServiceScopeFactory scopeFactory,
    IGrowwReadOnlyGateway gateway,
    IOptions<OptionResearchCaptureOptions> options,
    TimeProvider timeProvider,
    ILogger<GrowwOptionResearchCaptureService> logger) : BackgroundService
{
    private const string Source = "GrowwOptionChainApiV1";
    private static readonly TimeZoneInfo IndiaTimeZone = FindIndiaTimeZone();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Market[] Markets =
    [
        new("NIFTY", "NSE"),
        new("SENSEX", "BSE")
    ];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.IntervalSeconds), timeProvider);
        do
        {
            var now = timeProvider.GetUtcNow();
            if (IsCaptureWindow(now))
            {
                foreach (var market in Markets)
                {
                    try { await CaptureAsync(market, now, stoppingToken); }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                    catch (Exception exception) { LogCaptureFailed(logger, market.Underlying, exception); }
                }
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task CaptureAsync(Market market, DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var indiaNow = TimeZoneInfo.ConvertTime(observedAtUtc, IndiaTimeZone);
        var today = DateOnly.FromDateTime(indiaNow.Date);
        var underlying = await db.Instruments.SingleOrDefaultAsync(value =>
            value.Exchange == market.Exchange && value.TradingSymbol == market.Underlying &&
            value.Type == InstrumentType.Index && value.IsActive, cancellationToken);
        if (underlying is null)
        {
            LogInstrumentUnavailable(logger, market.Underlying);
            return;
        }

        var expiry = await db.Instruments
            .Where(value => value.Exchange == market.Exchange && value.IsActive &&
                            (value.Type == InstrumentType.CallOption || value.Type == InstrumentType.PutOption) &&
                            value.TradingSymbol.StartsWith(market.Underlying) && value.ExpiryDate >= today)
            .OrderBy(value => value.ExpiryDate)
            .Select(value => value.ExpiryDate)
            .FirstOrDefaultAsync(cancellationToken);
        if (expiry is null)
        {
            LogExpiryUnavailable(logger, market.Underlying);
            return;
        }

        // Minute-normalization makes restart/retry capture idempotent.
        var minuteUtc = new DateTimeOffset(observedAtUtc.Year, observedAtUtc.Month, observedAtUtc.Day,
            observedAtUtc.Hour, observedAtUtc.Minute, 0, TimeSpan.Zero);
        if (await db.OptionChainSnapshots.AnyAsync(value => value.UnderlyingInstrumentId == underlying.Id &&
                value.ExpiryDate == expiry && value.SourceTimestampUtc == minuteUtc && value.Source == Source,
            cancellationToken)) return;

        var chain = await gateway.GetOptionChainAsync(
            new GrowwOptionChainRequest(market.Exchange, market.Underlying, expiry.Value), cancellationToken);
        var nearest = chain.Strikes.OrderBy(value => Math.Abs(value.StrikePrice - chain.UnderlyingLastPrice))
            .Take(options.Value.StrikesEachSide * 2 + 1)
            .OrderBy(value => value.StrikePrice)
            .ToArray();
        var payload = JsonSerializer.Serialize(new ResearchPayload(
            market.Underlying, market.Exchange, expiry.Value, chain.UnderlyingLastPrice,
            false, chain.Strikes.Count, nearest), JsonOptions);
        db.OptionChainSnapshots.Add(new OptionChainSnapshot(Guid.NewGuid(), underlying.Id, expiry.Value,
            Source, minuteUtc, observedAtUtc, payload));
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception)) { }
    }

    private bool IsCaptureWindow(DateTimeOffset now)
    {
        var india = TimeZoneInfo.ConvertTime(now, IndiaTimeZone);
        if (india.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return false;
        var current = TimeOnly.FromDateTime(india.DateTime);
        return current >= TimeOnly.ParseExact(options.Value.SessionStart, "HH:mm") &&
               current <= TimeOnly.ParseExact(options.Value.SessionEnd, "HH:mm");
    }

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    private static TimeZoneInfo FindIndiaTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("India Standard Time"); }
    }

    private sealed record Market(string Underlying, string Exchange);
    private sealed record ResearchPayload(string Underlying, string Exchange, DateOnly ExpiryDate,
        decimal UnderlyingLastPrice, bool ProviderTimestampAvailable, int ProviderStrikeCount,
        IReadOnlyList<GrowwOptionStrike> SelectedStrikes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Groww option research capture failed for {Underlying}.")]
    private static partial void LogCaptureFailed(ILogger logger, string underlying, Exception exception);
    [LoggerMessage(Level = LogLevel.Warning, Message = "Option research underlying {Underlying} is not synchronized.")]
    private static partial void LogInstrumentUnavailable(ILogger logger, string underlying);
    [LoggerMessage(Level = LogLevel.Warning, Message = "No current option expiry is synchronized for {Underlying}.")]
    private static partial void LogExpiryUnavailable(ILogger logger, string underlying);
}
