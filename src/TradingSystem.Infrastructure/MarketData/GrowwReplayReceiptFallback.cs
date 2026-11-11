using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingSystem.Application.Broker;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Broker.Groww;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.Infrastructure.MarketData;

// Website only: reuse the foreground gateway and its shared REST budget. The
// recorder host does not schedule broker REST calls during streaming outages.
internal sealed class GrowwReplayReceiptFallback(IGrowwReadOnlyGateway broker, ReplayBlobStorage cloud,
    IHistoricalQuoteArchive archive, ResearchCapturePulse pulse, IOptions<HistoricalFeedCaptureOptions> feed,
    IServiceScopeFactory scopes, TimeProvider clock, ILogger<GrowwReplayReceiptFallback> logger) : BackgroundService
{
    private static readonly TimeSpan India = TimeSpan.FromHours(5.5);
    private static readonly TimeSpan MaximumHeartbeatAge = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan MaximumDepthAge = TimeSpan.FromSeconds(15);
    private readonly Dictionary<GrowwQuoteRequest, DateTimeOffset> attempted = [];
    private static readonly Action<ILogger, string, Exception?> Skipped = LoggerMessage.Define<string>(
        LogLevel.Debug, new EventId(6762, "GrowwReplayReceiptFallbackSkipped"),
        "Groww supplemental replay quote was skipped ({Reason}).");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!cloud.Enabled || broker is not GrowwReadOnlyGateway gateway) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5), clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var now = clock.GetUtcNow();
            if (!InWindow(now)) continue;
            try
            {
                var day = DateOnly.FromDateTime(now.ToOffset(India).DateTime);
                var snapshot = feed.Value.Enabled
                    ? new ReplayRecorderSnapshot(day, now, pulse.Read(day, true), archive.ReadHealth(day), null)
                    : await cloud.ReadRecorderHealthAsync(stoppingToken);
                if (snapshot is null || snapshot.Date != day) continue;
                await CaptureAsync(gateway, snapshot, clock.GetUtcNow(), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error) { Skipped(logger, error.GetType().Name, null); }
        }
    }

    private async Task CaptureAsync(GrowwReadOnlyGateway gateway, ReplayRecorderSnapshot snapshot, DateTimeOffset now,
        CancellationToken token)
    {
        if (!InWindow(now) || !Stalled(snapshot, "NIFTY", now) && !Stalled(snapshot, "SENSEX", now) && !Stalled(snapshot, "BANKNIFTY", now)) return;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var open = db.MarketPaperPositions.AsNoTracking().Where(value => value.Status == "Active" && value.RemainingQuantity > 0)
            .Select(value => value.ExecutionInstrumentId).Union(db.Positions.AsNoTracking()
                .Where(value => value.Quantity > 0 && (value.State == PositionState.Open || value.State == PositionState.ExitPending ||
                    value.State == PositionState.ReconciliationRequired)).Select(value => value.InstrumentId));
        var signals = db.Signals.AsNoTracking().Where(value => value.ExpiresAtUtc > now && value.MarketDataTimestampUtc <= now &&
            (value.Status == SignalStatus.Generated || value.Status == SignalStatus.Accepted)).Select(value => value.InstrumentId);
        var candidates = await db.Instruments.AsNoTracking().Where(value => (open.Contains(value.Id) || signals.Contains(value.Id)) &&
            value.Segment == InstrumentSegment.FuturesAndOptions && (value.Type == InstrumentType.CallOption || value.Type == InstrumentType.PutOption))
            .Select(value => new { Request = new GrowwQuoteRequest(value.Exchange, "FNO", value.TradingSymbol), Held = open.Contains(value.Id) })
            .ToArrayAsync(token);
        foreach (var entry in attempted.Where(value => now - value.Value > TimeSpan.FromMinutes(3)).ToArray()) attempted.Remove(entry.Key);
        var candidate = candidates.Where(value => Market(value.Request) is { } market && Stalled(snapshot, market, now) &&
                !gateway.HasRecentReplayReceipt(value.Request, now) && now - attempted.GetValueOrDefault(value.Request) >= TimeSpan.FromSeconds(12))
            .OrderByDescending(value => value.Held).ThenBy(value => attempted.GetValueOrDefault(value.Request))
            .Select(value => value.Request).FirstOrDefault();
        if (candidate is null) return;
        var accessToken = await scope.ServiceProvider.GetRequiredService<IGrowwTokenVault>().GetValidTokenAsync(token);
        if (string.IsNullOrWhiteSpace(accessToken)) return;
        attempted[candidate] = now;
        await gateway.TryRecordSupplementalQuoteAsync(candidate, accessToken, token);
    }

    internal static bool InWindow(DateTimeOffset now)
    {
        var local = now.ToOffset(India);
        var time = TimeOnly.FromDateTime(local.DateTime);
        return local.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && time >= new TimeOnly(9, 15, 30) && time < new TimeOnly(15, 30);
    }

    internal static bool Stalled(ReplayRecorderSnapshot snapshot, string market, DateTimeOffset now) =>
        snapshot.AtUtc <= now && (now - snapshot.AtUtc > MaximumHeartbeatAge ||
            snapshot.Storage is not { } storage || !storage.LastOptionSourceAtByMarket.TryGetValue(market, out var source) ||
            source > now || now - source > MaximumDepthAge);

    internal static string? Market(GrowwQuoteRequest request)
    {
        if (request.Segment != "FNO" || !request.TradingSymbol.EndsWith("CE", StringComparison.Ordinal) &&
            !request.TradingSymbol.EndsWith("PE", StringComparison.Ordinal)) return null;
        if (request.Exchange == "BSE" && request.TradingSymbol.StartsWith("SENSEX", StringComparison.Ordinal)) return "SENSEX";
        if (request.Exchange != "NSE") return null;
        if (request.TradingSymbol.StartsWith("BANKNIFTY", StringComparison.Ordinal)) return "BANKNIFTY";
        return request.TradingSymbol.StartsWith("NIFTY", StringComparison.Ordinal) ? "NIFTY" : null;
    }
}
