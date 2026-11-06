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
    private string? failure;
    private string stage = "Waiting";
    private bool policyReady;
    private bool priorReady;
    private readonly Dictionary<string, DateTimeOffset> receipts = new(StringComparer.Ordinal);

    internal void Starting(DateOnly session)
    {
        lock (gate)
        {
            if (session != date) { gap = false; failure = null; }
            date = session; phase = "Connecting"; instruments = 0; subscribed = null; receipts.Clear();
            policyReady = false; priorReady = false;
        }
    }

    internal void Subscribed(DateTimeOffset at, int count)
    { lock (gate) { subscribed = at; instruments = count; phase = "Subscribed"; } }
    internal void Received(string market, DateTimeOffset at)
    { lock (gate) { if (at > receipts.GetValueOrDefault(market)) receipts[market] = at; } }
    internal void Failed()
    { lock (gate) { gap = true; phase = "Interrupted"; } }
    internal void Stage(string value) { lock (gate) stage = value; }
    internal void Metadata(bool policy, bool prior) { lock (gate) { policyReady = policy; priorReady = prior; } }
    internal void Failed(string code, bool gapObserved = true)
    { lock (gate) { gap |= gapObserved; phase = "Interrupted"; failure = stage + ": " + code; } }
    internal void Finished()
    { lock (gate) phase = "Finished"; }

    public ResearchCaptureSnapshot Read(DateOnly session, bool enabled)
    {
        lock (gate)
        {
            if (!enabled || date != session) return new(!enabled ? "Disabled" : "Not started", null, 0, false, new Dictionary<string,DateTimeOffset>());
            return new(phase, subscribed, instruments, gap, new Dictionary<string,DateTimeOffset>(receipts))
                { LastFailure = failure, Stage = stage, PreOpenPolicyReady = policyReady, PriorSensexStateReady = priorReady };
        }
    }
}

public sealed record ResearchCaptureSnapshot(string Phase, DateTimeOffset? SubscribedAtUtc,
    int Instruments, bool GapObserved, IReadOnlyDictionary<string, DateTimeOffset> LastReceiptByMarket)
{
    private static readonly string[] Markets = ["NIFTY", "SENSEX", "BANKNIFTY"];
    public string? LastFailure { get; init; }
    public string Stage { get; init; } = "Waiting";
    public bool PreOpenPolicyReady { get; init; }
    public bool PriorSensexStateReady { get; init; }
    public bool HasStalledMarket(DateTimeOffset now) => SubscribedAtUtc is { } subscribed &&
        now - subscribed > TimeSpan.FromSeconds(30) &&
        Markets.Any(market =>
            !LastReceiptByMarket.TryGetValue(market, out var last) || now - last > TimeSpan.FromSeconds(30));
}
