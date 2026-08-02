using System.Collections.Concurrent;

namespace TradingSystem.Application.MarketData;

public sealed class MarketDataHealthMonitor : IMarketDataHealthReader
{
    private readonly ConcurrentDictionary<string, MutableHealth> providers = new(StringComparer.Ordinal);

    public void Record(string provider, DateTimeOffset observedAtUtc, MarketDataValidationResult result)
    {
        var health = providers.GetOrAdd(provider, _ => new());
        lock (health)
        {
            health.LastObservationUtc = observedAtUtc;
            health.Available = result.Accepted;
            if (result.Reason is MarketDataRejectionReason.SequenceGap or MarketDataRejectionReason.OutOfOrder)
                health.RequiresReset = true;
            health.TradingPermitted = result.TradingPermitted && !health.RequiresReset;
            health.LastRejection = result.Accepted ? null : result.Reason;
            if (result.Accepted) health.AcceptedCount++; else health.RejectedCount++;
        }
    }

    public void ResetAfterReconciliation(string provider)
    {
        if (!providers.TryGetValue(provider, out var health)) return;
        lock (health)
        {
            health.RequiresReset = false;
            health.TradingPermitted = health.Available;
        }
    }

    public IReadOnlyList<MarketDataHealthSnapshot> GetCurrent() => providers
        .OrderBy(pair => pair.Key, StringComparer.Ordinal)
        .Select(pair => Snapshot(pair.Key, pair.Value)).ToArray();

    private static MarketDataHealthSnapshot Snapshot(string provider, MutableHealth health)
    {
        lock (health)
            return new(provider, health.Available, health.TradingPermitted, health.LastObservationUtc,
                health.LastRejection, health.AcceptedCount, health.RejectedCount);
    }

    private sealed class MutableHealth
    {
        public bool Available;
        public bool TradingPermitted;
        public DateTimeOffset? LastObservationUtc;
        public MarketDataRejectionReason? LastRejection;
        public long AcceptedCount;
        public long RejectedCount;
        public bool RequiresReset;
    }
}
