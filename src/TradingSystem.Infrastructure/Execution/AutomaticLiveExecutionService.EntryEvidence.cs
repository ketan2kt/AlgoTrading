using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingSystem.Application.Risk;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.Infrastructure.Execution;

internal sealed partial class AutomaticLiveExecutionService
{
    // Best effort, own DbContext: a telemetry failure cannot flush/change order state.
    private readonly HashSet<Guid> recordedEntryReasons = [];
    private DateOnly entryReasonDate;
    private readonly Dictionary<string,DateTimeOffset> inspectedThrough = new(StringComparer.Ordinal);
    private readonly Dictionary<string,DateTimeOffset> lastDeferredScan = new(StringComparer.Ordinal);
    internal const string ExpiredObservation = "Paper entry was outside the live freshness window at inspection; this does not establish why an earlier cycle missed it.";
    private static string? ContractRoutingIssue(Instrument? instrument, string exchange, int quantity)
    {
        if (instrument is null) return "Execution contract was missing or inactive.";
        if (instrument.Exchange != exchange) return $"Execution contract exchange was {instrument.Exchange}; expected {exchange}.";
        if (quantity <= 0) return "Paper quantity was not positive.";
        if (instrument.LotSize <= 0) return "Contract had no positive lot size.";
        return quantity % instrument.LotSize != 0 ? $"Paper quantity {quantity} was not a multiple of lot size {instrument.LotSize}." : null;
    }
    private async Task NoteEntryAsync(string market, Guid source, Guid instrument, string reason,
        CancellationToken token)
    {
        var now = timeProvider.GetUtcNow();
        var date = DateOnly.FromDateTime(now.UtcDateTime);
        if (date != entryReasonDate) { recordedEntryReasons.Clear(); entryReasonDate = date; }
        var id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"entry-evidence:v1:{market}:{source:N}:{reason}")).AsSpan(0,16));
        if (recordedEntryReasons.Contains(id)) return;
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var evidence = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            if (!await evidence.MarketStrategyAudits.AsNoTracking().AnyAsync(x => x.Id == id, token))
            {
                evidence.MarketStrategyAudits.Add(new MarketStrategyAudit(id,market,instrument,now,
                    $"LiveEntryDecision:{source:N}",0m,JsonSerializer.Serialize(new {reason})));
                await evidence.SaveChangesAsync(token);
            }
            recordedEntryReasons.Add(id);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { LogEntryEvidenceFailed(logger, ex.GetType().Name); }
    }

    // Inspect only the newly expired interval, after discovery found nothing to
    // submit. Never replay old entries or turn the audit into a broker action.
    private async Task NoteExpiredEntriesAsync(TradingDbContext db, LiveTradingArmStatus arm, CancellationToken token)
    {
        try
        {
            var now = timeProvider.GetUtcNow();
            var local = now.ToOffset(TimeSpan.FromHours(5.5));
            if (local.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ||
                local.TimeOfDay < TimeSpan.FromHours(9) || local.TimeOfDay > TimeSpan.FromHours(16)) return;
            var dayStart = new DateTimeOffset(local.Date, local.Offset).ToUniversalTime();
            var until = now.AddSeconds(-options.Value.SignalMaximumAgeSeconds);
            foreach (var m in arm.Markets.Where(x => x.Armed && x.ChangedAtUtc.HasValue))
            {
                var market = m.Market.ToLowerInvariant();
                var since = m.ChangedAtUtc!.Value > dayStart ? m.ChangedAtUtc.Value : dayStart;
                if (inspectedThrough.TryGetValue(market,out var last) && last > since) since = last;
                if (until - since < TimeSpan.FromMinutes(1)) continue;
                var positions = await db.MarketPaperPositions.AsNoTracking().Where(x => x.Market == market &&
                    x.OpenedAtUtc >= since && x.OpenedAtUtc < until && !db.LiveExecutionIntents.Any(i => i.SourceId == x.Id))
                    .Select(x => new {x.Id,x.UnderlyingInstrumentId}).ToArrayAsync(token);
                foreach (var x in positions)
                    await NoteEntryAsync(market,x.Id,x.UnderlyingInstrumentId,ExpiredObservation,token);
                if (market == "nifty")
                {
                    var refs = await db.PaperBrokerEvents.AsNoTracking().Where(x => x.EventType == "OrderFilled" &&
                        x.ClientReference.EndsWith("-ENTRY") && x.OccurredAtUtc >= since && x.OccurredAtUtc < until)
                        .Select(x => x.ClientReference).ToArrayAsync(token);
                    var ids = refs.Select(r => TryParseNiftyEntrySignalId("OrderFilled",r,out var id) ? id : Guid.Empty).ToArray();
                    if (ids.Length > 0)
                    {
                        var signals = await db.Signals.AsNoTracking().Where(x => ids.Contains(x.Id) &&
                            !db.LiveExecutionIntents.Any(i => i.SourceId == x.Id)).Select(x => new {x.Id,x.InstrumentId}).ToArrayAsync(token);
                        foreach (var x in signals) await NoteEntryAsync(market,x.Id,x.InstrumentId,ExpiredObservation,token);
                    }
                }
                inspectedThrough[market] = until;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { LogEntryEvidenceFailed(logger, ex.GetType().Name); }
    }

    private async Task NoteDeferredEntriesAsync(TradingDbContext db, LiveTradingArmStatus arm, string reason,
        CancellationToken token)
    {
        var now = timeProvider.GetUtcNow();
        var spacing = TimeSpan.FromSeconds(Math.Clamp(options.Value.SignalMaximumAgeSeconds / 2, 1, 5));
        if (lastDeferredScan.TryGetValue(reason,out var last) && now - last < spacing) return;
        lastDeferredScan[reason] = now;
        try
        {
            var cutoff = now.AddSeconds(-options.Value.SignalMaximumAgeSeconds);
            foreach (var m in arm.Markets.Where(x => x.Armed && x.ChangedAtUtc.HasValue))
            {
                var market = m.Market.ToLowerInvariant();
                var since = m.ChangedAtUtc!.Value > cutoff ? m.ChangedAtUtc.Value : cutoff;
                var positions = await db.MarketPaperPositions.AsNoTracking().Where(x =>
                    x.Market == market && x.Status == "Active" && x.OpenedAtUtc >= since &&
                    !db.LiveExecutionIntents.Any(i => i.SourceId == x.Id))
                    .Select(x => new {x.Id,x.UnderlyingInstrumentId}).ToListAsync(token);
                foreach (var x in positions) await NoteEntryAsync(market,x.Id,x.UnderlyingInstrumentId,reason,token);
                if (market != "nifty") continue;
                var fills = await db.PaperBrokerEvents.AsNoTracking().Where(x => x.EventType == "OrderFilled" &&
                    x.ClientReference.EndsWith("-ENTRY") && x.OccurredAtUtc >= since)
                    .Select(x => x.ClientReference).ToArrayAsync(token);
                var ids = fills.Select(x => TryParseNiftyEntrySignalId("OrderFilled", x, out var id) ? id : Guid.Empty)
                    .Where(id => id != Guid.Empty).ToArray();
                if (ids.Length == 0) continue;
                var signals = await db.Signals.AsNoTracking().Where(x => ids.Contains(x.Id) &&
                    !db.LiveExecutionIntents.Any(i => i.SourceId == x.Id))
                    .Select(x => new {x.Id,x.InstrumentId}).ToArrayAsync(token);
                foreach (var x in signals) await NoteEntryAsync(market,x.Id,x.InstrumentId,reason,token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { LogEntryEvidenceFailed(logger, ex.GetType().Name); }
    }

    [LoggerMessage(LogLevel.Warning, "Live entry decision evidence unavailable ({failureType}).")]
    private static partial void LogEntryEvidenceFailed(ILogger logger, string failureType);
}
