namespace TradingSystem.Infrastructure.MarketData;

// Process-local observations only: a restart must not resurrect a healthy connection.
public sealed class ResearchCapturePulse
{
    private readonly object gate = new();
    private DateOnly date;
    private string phase = "Not started";
    private DateTimeOffset? subscribed;
    private bool gap;
    private int instruments;
    private readonly Dictionary<string, DateTimeOffset> receipts = new(StringComparer.Ordinal);

    internal void Starting(DateOnly session)
    {
        lock (gate)
        {
            if (session != date) gap = false;
            date = session; phase = "Connecting"; instruments = 0; subscribed = null; receipts.Clear();
        }
    }

    internal void Subscribed(DateTimeOffset at, int count)
    { lock (gate) { subscribed = at; instruments = count; phase = "Subscribed"; } }
    internal void Received(string market, DateTimeOffset at)
    { lock (gate) { if (at > receipts.GetValueOrDefault(market)) receipts[market] = at; } }
    internal void Failed()
    { lock (gate) { gap = true; phase = "Interrupted"; } }
    internal void Finished()
    { lock (gate) phase = "Finished"; }

    public ResearchCaptureSnapshot Read(DateOnly session, bool enabled)
    {
        lock (gate)
        {
            if (!enabled || date != session) return new(!enabled ? "Disabled" : "Not started", null, 0, false, new Dictionary<string,DateTimeOffset>());
            return new(phase, subscribed, instruments, gap, new Dictionary<string,DateTimeOffset>(receipts));
        }
    }
}

public sealed record ResearchCaptureSnapshot(string Phase, DateTimeOffset? SubscribedAtUtc,
    int Instruments, bool GapObserved, IReadOnlyDictionary<string, DateTimeOffset> LastReceiptByMarket);
