using System.Globalization;
using System.Security.Cryptography;
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
using TradingSystem.Infrastructure.Execution;

namespace TradingSystem.Infrastructure.MarketData;

public sealed class HistoricalFeedCaptureOptions
{
    public const string SectionName = "MarketData:HistoricalReplayFeed";
    public bool Enabled { get; init; } = true;
    public bool AfterMarketBackfillEnabled { get; init; } = true;
    public int MaximumInstruments { get; init; } = 1000;
    public int SampleSeconds { get; init; } = 2;
}

// A research-only sibling of the trading REST feeds. No orders, strategies or
// controls are resolved. Failure only makes the research day ineligible.
internal sealed class GrowwHistoricalFeedCaptureService(IServiceScopeFactory scopes,
    IGrowwHistoricalFeedTransport transport, IHistoricalQuoteArchive archive,
    IOptions<HistoricalFeedCaptureOptions> options, TimeProvider clock,
    ILogger<GrowwHistoricalFeedCaptureService> logger) : BackgroundService
{
    private static readonly TimeSpan India = TimeSpan.FromHours(5.5);
    private static readonly Action<ILogger, string, Exception?> CaptureFailed = LoggerMessage.Define<string>(
        LogLevel.Warning, new EventId(6753, "HistoricalFeedCaptureFailed"),
        "Historical streaming capture failed ({FailureType}); a retry is scheduled and research coverage is incomplete.");
    private readonly string root = Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? AppContext.BaseDirectory,
        "data", "historical-replay", "feed");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            var local = clock.GetUtcNow().ToOffset(India);
            if (local.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ||
                TimeOnly.FromDateTime(local.DateTime) < new TimeOnly(9, 0) ||
                TimeOnly.FromDateTime(local.DateTime) >= new TimeOnly(15, 35))
            { await Task.Delay(TimeSpan.FromSeconds(30), clock, stoppingToken); continue; }
            var date = DateOnly.FromDateTime(local.DateTime);
            try { await CaptureSessionAsync(date, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                archive.MarkCaptureGap(date);
                // Never log transport/auth exception messages or NATS options:
                // credentials may be embedded in third-party error details.
                CaptureFailed(logger, exception.GetType().Name, null);
                await Task.Delay(TimeSpan.FromSeconds(30), clock, stoppingToken);
            }
        }
    }

    internal async Task CaptureSessionAsync(DateOnly date, CancellationToken stoppingToken)
    {
        var now = clock.GetUtcNow();
        var plan = await LoadPlanAsync(date, stoppingToken);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(plan);
        var fingerprint = Convert.ToHexString(SHA256.HashData(bytes));
        var manifest = new HistoricalFeedCaptureManifest(date, now, null, null, fingerprint,
            options.Value.SampleSeconds, now > new DateTimeOffset(date.ToDateTime(new TimeOnly(9, 15)), India), plan)
        { SensexPriorThesisStateCaptured = true, PriorSensexThesis = await LoadPriorThesisAsync(date, stoppingToken) };
        if (manifest.HasGaps) archive.MarkCaptureGap(date);
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N") + ".json");
        await WriteManifestAsync(path, manifest, stoppingToken);
        var accumulator = new GrowwHistoricalFeedAccumulator(archive);
        var end = new DateTimeOffset(date.ToDateTime(new TimeOnly(15, 35)), India).ToUniversalTime();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var timer = Task.Delay(end - now, clock, lifetime.Token);
        var failed = false;
        DateTimeOffset? subscribedAt = null;
        var consume = transport.ConsumeAsync(plan, accumulator.Observe,
            () => { failed = true; archive.MarkCaptureGap(date); },
            at => subscribedAt = at, lifetime.Token);
        var sampling = SampleAsync(accumulator, lifetime.Token);
        try
        {
            var completed = await Task.WhenAny(consume, timer, sampling);
            if (completed == sampling) await sampling;
            if (completed == consume)
            {
                await consume;
                throw new InvalidDataException("Historical feed stopped before the session window ended.");
            }
            await timer;
            await lifetime.CancelAsync();
            try { await consume; } catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            manifest = manifest with { SubscribedAtUtc = subscribedAt, CompletedAtUtc = clock.GetUtcNow(),
                HasGaps = manifest.HasGaps || failed || subscribedAt is null ||
                    subscribedAt > new DateTimeOffset(date.ToDateTime(new TimeOnly(9, 15)), India) };
        }
        catch
        {
            manifest = manifest with { SubscribedAtUtc = subscribedAt, HasGaps = true };
            archive.MarkCaptureGap(date);
            throw;
        }
        finally
        {
            await lifetime.CancelAsync();
            try { await consume; }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception) when (consume.IsFaulted) { /* Already observed by the main await; retain the failure manifest. */ }
            try { await sampling; }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception) when (sampling.IsFaulted) { manifest = manifest with { HasGaps = true }; }
            await WriteManifestAsync(path, manifest, CancellationToken.None);
        }
    }

    private async Task SampleAsync(GrowwHistoricalFeedAccumulator accumulator, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.SampleSeconds), clock);
        while (await timer.WaitForNextTickAsync(token)) accumulator.Sample(clock.GetUtcNow());
    }

    private async Task<IReadOnlyList<HistoricalFeedInstrument>> LoadPlanAsync(DateOnly date, CancellationToken token)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var rows = await db.Instruments.AsNoTracking().Where(value => value.IsActive &&
            (value.Exchange == "NSE" || value.Exchange == "BSE") &&
            (value.TradingSymbol.StartsWith("NIFTY") || value.TradingSymbol.StartsWith("BANKNIFTY") || value.TradingSymbol.StartsWith("SENSEX")) &&
            (value.Type == InstrumentType.Index || value.Type == InstrumentType.Future ||
             value.Type == InstrumentType.CallOption || value.Type == InstrumentType.PutOption)).ToArrayAsync(token);
        return HistoricalFeedSubscriptionPlanner.Select(date, rows.Select(value => new HistoricalFeedInstrument(
            value.TradingSymbol.StartsWith("BANKNIFTY", StringComparison.Ordinal) ? "BANKNIFTY" :
                value.TradingSymbol.StartsWith("SENSEX", StringComparison.Ordinal) ? "SENSEX" : "NIFTY",
            value.Exchange, value.Segment == InstrumentSegment.Cash ? "CASH" : "FNO", value.ExchangeToken ?? "",
            value.TradingSymbol, value.Type, value.ExpiryDate, value.StrikePrice, value.LotSize, value.TickSize)).ToArray(),
            options.Value.MaximumInstruments);
    }

    private async Task<PersistentDirectionalThesisV1?> LoadPriorThesisAsync(DateOnly date, CancellationToken token)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var start = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), India).ToUniversalTime();
        var underlyingId = await db.Instruments.AsNoTracking().Where(value => value.Exchange == "BSE" &&
                value.Type == InstrumentType.Index && value.TradingSymbol == "SENSEX")
            .Select(value => value.Id).SingleAsync(token);
        var json = await db.MarketStrategyAudits.AsNoTracking().Where(value => value.Market == "sensex" &&
                value.UnderlyingInstrumentId == underlyingId &&
                value.CandleTimeUtc < start && value.Outcome == PersistentDirectionalThesisAnalyzerV1.AuditOutcome)
            .OrderByDescending(value => value.CandleTimeUtc).Select(value => value.ReasonsJson).FirstOrDefaultAsync(token);
        return DirectionalThesisShadowAudit.Read(json);
    }

    private static async Task WriteManifestAsync(string path, HistoricalFeedCaptureManifest manifest, CancellationToken token)
    {
        var pending = path + ".pending";
        await File.WriteAllBytesAsync(pending, JsonSerializer.SerializeToUtf8Bytes(manifest), token);
        File.Move(pending, path, overwrite: true);
    }
}
