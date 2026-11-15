using TradingSystem.Application.Strategies;

namespace TradingSystem.Application.Execution;

// A replay frame owns an immutable completed-bar array. Quote ticks do not
// change it. Keep only the latest projection, never a process-wide/session cache.
internal sealed class HistoricalReplayBarProjection(string symbol, int intervalSeconds, bool requireReceipt)
{
    private static readonly TimeSpan IndiaOffset = TimeSpan.FromHours(5.5);
    private HistoricalReplayBar[]? input;
    private DateTimeOffset validFrom;
    private DateTimeOffset validUntil;
    private StrategyPriceBar[] all = [];
    private StrategyPriceBar[] current = [];

    public StrategyPriceBar[] Get(HistoricalReplayFrame frame, bool currentSession)
    {
        var at = frame.AvailableAtUtc;
        // Mutable/custom lists are deliberately not cached. Rewinding time or
        // reaching a delayed receipt/candle close also invalidates the result.
        if (frame.CompletedBars is not HistoricalReplayBar[] array ||
            !ReferenceEquals(array, input) || at < validFrom || at >= validUntil)
        {
            var day = at.ToOffset(IndiaOffset).Date;
            validFrom = at;
            validUntil = new DateTimeOffset(day.AddDays(1), IndiaOffset);
            var visible = new List<StrategyPriceBar>();
            foreach (var bar in frame.CompletedBars)
            {
                if (bar.Symbol != symbol || bar.IntervalSeconds != intervalSeconds) continue;
                var available = bar.OpenTimeUtc.AddSeconds(intervalSeconds);
                if (requireReceipt && bar.AvailableAtUtc > available) available = bar.AvailableAtUtc;
                if (available > at)
                {
                    if (available < validUntil) validUntil = available;
                    continue;
                }
                visible.Add(new(bar.OpenTimeUtc, bar.Open, bar.High, bar.Low, bar.Close));
            }
            all = visible.OrderBy(bar => bar.OpenTimeUtc).ToArray();
            current = all.Where(bar => bar.OpenTimeUtc.ToOffset(IndiaOffset).Date == day).ToArray();
            input = frame.CompletedBars as HistoricalReplayBar[];
        }
        return currentSession ? current : all;
    }
}
