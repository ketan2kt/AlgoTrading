using TradingSystem.Application.Broker;
using TradingSystem.Application.Execution;

namespace TradingSystem.Infrastructure.MarketData;

// Recorder-side, independent of browser traffic and scheduled Codex jobs.
// Only persisted, sized, two-sided option books can advance this clock.
internal sealed class ReplayDepthMonitor(DateTimeOffset open, DateTimeOffset close)
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);
    private readonly Dictionary<string, MarketState> markets = new(StringComparer.Ordinal)
    {
        ["NIFTY"] = new(), ["SENSEX"] = new(), ["BANKNIFTY"] = new()
    };
    private bool finished;

    internal bool Check(DateTimeOffset now, HistoricalQuoteArchiveHealth? health)
    {
        if (now < open || finished) return false;
        var at = now < close ? now : close;
        var outage = false;
        foreach (var (market, state) in markets)
        {
            if (health?.LastOptionWrittenAtByMarket.TryGetValue(market, out var receipt) == true &&
                health.LastOptionSourceAtByMarket.TryGetValue(market, out var source) &&
                receipt >= open && receipt <= at && source <= receipt && receipt - source <= Limit &&
                (state.LastReceipt is null || receipt > state.LastReceipt))
            {
                var previous = state.LastReceipt ?? open;
                if (receipt - previous > Limit) state.Gaps.Add(new(previous, receipt));
                state.LastReceipt = receipt;
                state.LastSource = source;
            }
            var start = state.LastReceipt ?? open;
            state.OpenGap = at - start > Limit ? new(start, at) : null;
            outage |= state.OpenGap is not null || state.Gaps.Count > 0;
        }
        finished = now >= close;
        return outage;
    }

    internal IReadOnlyList<HistoricalDepthMarketAudit> Snapshot() => markets
        .Select(pair => new HistoricalDepthMarketAudit(pair.Key, pair.Value.LastReceipt, pair.Value.LastSource,
            pair.Value.OpenGap is { } gap ? [.. pair.Value.Gaps, gap] : pair.Value.Gaps.ToArray()))
        .ToArray();

    private sealed class MarketState
    {
        internal DateTimeOffset? LastReceipt { get; set; }
        internal DateTimeOffset? LastSource { get; set; }
        internal HistoricalDepthGap? OpenGap { get; set; }
        internal List<HistoricalDepthGap> Gaps { get; } = [];
    }
}
