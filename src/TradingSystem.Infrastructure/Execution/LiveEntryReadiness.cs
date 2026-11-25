using System.Collections.Concurrent;
using TradingSystem.Infrastructure.MarketData;

namespace TradingSystem.Infrastructure.Execution;

// Display-only worker receipts. These never arm entries or override risk gates.
internal sealed class LiveEntryReadiness
{
    internal sealed record Receipt(string Status, string Reason, DateTimeOffset AtUtc);
    private readonly ConcurrentDictionary<(string Market, string Broker), Receipt> receipts = new();
    internal void Report(string market, string broker, string status, string reason, DateTimeOffset now) =>
        receipts[(PaperDataSource.Canonical(market), broker)] = new(status, reason, now);
    internal Receipt Read(string market, string broker, DateTimeOffset now) =>
        receipts.TryGetValue((PaperDataSource.Canonical(market), broker), out var value) &&
        value.AtUtc <= now && now - value.AtUtc <= TimeSpan.FromSeconds(30)
            ? value : new("Unknown", "Awaiting a current execution-worker check.", now);
    internal void BlockBroker(string broker, string reason, DateTimeOffset now)
    {
        foreach (var key in receipts.Keys.Where(x => x.Broker == broker))
            receipts[key] = new("Blocked", reason, now);
    }
}
