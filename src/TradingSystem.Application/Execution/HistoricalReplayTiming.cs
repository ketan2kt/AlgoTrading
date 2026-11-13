using System.Diagnostics;

namespace TradingSystem.Application.Execution;

public readonly record struct HistoricalReplayTimingSample(string Stage, DateTimeOffset? MarketTimeUtc,
    int QuotesProcessed, int OpenPositions, long ElapsedMs, string Totals);

// One accumulator per market; only a bounded set of stages/operations is used.
// No per-tick logging or market data payloads are emitted.
public sealed class HistoricalReplayTiming(Action<HistoricalReplayTimingSample> publish)
{
    private readonly long started = Stopwatch.GetTimestamp();
    private long lastProgress = Stopwatch.GetTimestamp();
    private readonly Dictionary<string, (long Calls, long Ticks)> totals = new(StringComparer.Ordinal);

    public Measurement Measure(string operation) => new(this, operation);

    public void Checkpoint(string stage, DateTimeOffset? marketTime = null, int quotes = 0, int open = 0) =>
        publish(new(stage, marketTime, quotes, open, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            string.Join(", ", totals.OrderByDescending(pair => pair.Value.Ticks)
                .Select(pair => FormattableString.Invariant($"{pair.Key}={TimeSpan.FromSeconds((double)pair.Value.Ticks / Stopwatch.Frequency).TotalMilliseconds:F0}ms/{pair.Value.Calls}")))));

    internal void Progress(DateTimeOffset marketTime, int quotes, int open)
    {
        var now = Stopwatch.GetTimestamp();
        if (Stopwatch.GetElapsedTime(lastProgress, now) < TimeSpan.FromSeconds(30)) return;
        lastProgress = now;
        Checkpoint("simulation-progress", marketTime, quotes, open);
    }

    public readonly struct Measurement(HistoricalReplayTiming owner, string operation) : IDisposable
    {
        private readonly long start = Stopwatch.GetTimestamp();

        public void Dispose()
        {
            var prior = owner.totals.GetValueOrDefault(operation);
            owner.totals[operation] = (prior.Calls + 1, prior.Ticks + Stopwatch.GetTimestamp() - start);
        }
    }
}
