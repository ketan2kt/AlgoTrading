using System.Collections.Concurrent;
using TradingSystem.Application.Execution;

namespace TradingSystem.Infrastructure.Execution;

internal sealed class HeroZeroMonitorState(TimeProvider timeProvider) : IHeroZeroMonitorReader
{
    private readonly ConcurrentDictionary<string, HeroZeroMonitorSnapshot> values =
        new(StringComparer.OrdinalIgnoreCase);

    public HeroZeroMonitorSnapshot GetSnapshot(string market) => values.GetValueOrDefault(market) ??
        new(market, false, null, "Unavailable", "The expiry monitor has not completed its first scan.",
            timeProvider.GetUtcNow(), null, null, null, [], 0m, 0m, 0m);

    public void Set(HeroZeroMonitorSnapshot snapshot) => values[snapshot.Market] = snapshot;
}
