using Microsoft.EntityFrameworkCore;
using TradingSystem.Infrastructure.Execution;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.Infrastructure.Diagnostics;

internal static class DailyLiveRouteHistory
{
    private static readonly bool[] BaselineAndSwitch = [true, false];
    internal sealed record Change(string Key, string Action, DateTimeOffset At);
    internal static string Key(string market) => EfLiveTradingArmService.KeyPrefix +
        (market.Equals("BANKNIFTY",StringComparison.OrdinalIgnoreCase) ? "BANK-NIFTY" : market.ToUpperInvariant());

    internal static async Task<IReadOnlyList<Change>> ReadAsync(TradingDbContext db, string[] keys,
        DateTimeOffset from, DateTimeOffset until, CancellationToken token)
    {
        if (keys.Length == 0) return [];
        const string prefix = LiveRoutingBaselineService.Prefix;
        var query = db.AuditLogs.AsNoTracking().Where(a => keys.Contains(a.EntityId) &&
            (a.Action.StartsWith(prefix) || a.Action == "LiveExecutionArmed" || a.Action == "LiveExecutionDisarmed"));
        var rows = await query.Where(a => a.OccurredAtUtc >= from && a.OccurredAtUtc < until)
            .Select(a => new Change(a.EntityId,a.Action,a.OccurredAtUtc)).ToListAsync(token);
        foreach (var key in keys)
        {
            var prior = query.Where(a => a.EntityId == key && a.OccurredAtUtc < from);
            foreach (var baseline in BaselineAndSwitch)
            {
                var last = await prior.Where(a => a.Action.StartsWith(prefix) == baseline)
                    .OrderByDescending(a => a.OccurredAtUtc).Select(a => new Change(a.EntityId,a.Action,a.OccurredAtUtc))
                    .FirstOrDefaultAsync(token);
                if (last is not null) rows.Add(last);
            }
        }
        return rows;
    }

    internal static string Explain(string market, DateTimeOffset at, IEnumerable<Change> changes)
    {
        var applicable = changes.Where(x => x.Key == Key(market) && x.At <= at).OrderByDescending(x => x.At).ToArray();
        var baseline = applicable.FirstOrDefault(x => x.Action.StartsWith(LiveRoutingBaselineService.Prefix,StringComparison.Ordinal));
        if (baseline?.Action == "LiveRouteBaseline:BuildDisabled") return "Live execution was disabled by the recorded server configuration at paper fill.";
        if (baseline?.Action == "LiveRouteBaseline:MarketExcluded") return "Market was not enabled in the recorded server configuration at paper fill.";
        return applicable.FirstOrDefault()?.Action switch
        {
            "LiveRouteBaseline:Off" or "LiveExecutionDisarmed" => "Live switch was OFF at paper fill.",
            "LiveRouteBaseline:On" or "LiveExecutionArmed" => "Live switch was ON at paper fill. No source-specific discovery outcome recorded; cause unknown.",
            _ => "No dated live-switch or discovery evidence at paper fill; cause unknown."
        };
    }
}
