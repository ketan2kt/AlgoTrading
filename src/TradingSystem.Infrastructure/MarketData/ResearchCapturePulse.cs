using TradingSystem.Application.Execution;

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
    private HistoricalFeedFrameRejection? lastRejectedFrame;
    private string stage = "Waiting";
    private bool policyReady;
    private bool priorReady;
    private ReplayContractPreflight? coverage;
    private IReadOnlyList<string> coverageIssues = [];
    private readonly Dictionary<string, DateTimeOffset> receipts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> optionPrices = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> optionDepth = new(StringComparer.Ordinal);
    private string[] requiredOptionMarkets = [];

    internal void Starting(DateOnly session)
    {
        lock (gate)
        {
            if (session != date) { gap = false; failure = null; lastRejectedFrame = null; }
            date = session; phase = "Connecting"; instruments = 0; subscribed = null; receipts.Clear();
            optionPrices.Clear(); optionDepth.Clear(); requiredOptionMarkets = [];
            policyReady = false; priorReady = false;
            coverage = null;
            coverageIssues = [];
        }
    }

    internal void Subscribed(DateTimeOffset at, int count, string[]? optionMarkets = null)
    { lock (gate) { subscribed = at; instruments = count; phase = "Subscribed"; requiredOptionMarkets = optionMarkets ?? []; } }
    internal void Received(string market, DateTimeOffset at)
    { lock (gate) { if (at > receipts.GetValueOrDefault(market)) receipts[market] = at; } }
    internal void OptionReceived(string market, string kind, DateTimeOffset at)
    {
        lock (gate)
        {
            var target = kind == "depth" ? optionDepth : optionPrices;
            if (at > target.GetValueOrDefault(market)) target[market] = at;
        }
    }
    internal void Failed()
    { lock (gate) { gap = true; phase = "Interrupted"; } }
    internal void Stage(string value) { lock (gate) stage = value; }
    internal void Metadata(bool policy, bool prior) { lock (gate) { policyReady = policy; priorReady = prior; } }
    internal void ContractCoverage(ReplayContractPreflight value) { lock (gate) coverage = value; }
    internal void ContractCoverageIssues(IReadOnlyList<string> values) { lock (gate) coverageIssues = values; }
    internal void Failed(string code, bool gapObserved = true)
    { lock (gate) { gap |= gapObserved; phase = "Interrupted"; failure = stage + ": " + code; } }
    internal void RejectedFrame(HistoricalFeedFrameRejection rejection, bool gapObserved)
    {
        lock (gate)
        {
            gap |= gapObserved;
            if (lastRejectedFrame is null || rejection.AtUtc >= lastRejectedFrame.AtUtc)
                lastRejectedFrame = rejection;
        }
    }
    internal void Finished()
    { lock (gate) phase = "Finished"; }

    public ResearchCaptureSnapshot Read(DateOnly session, bool enabled)
    {
        lock (gate)
        {
            if (!enabled || date != session) return new(!enabled ? "Disabled" : "Not started", null, 0, false, new Dictionary<string,DateTimeOffset>());
            return new(phase, subscribed, instruments, gap, new Dictionary<string,DateTimeOffset>(receipts))
                { LastFailure = failure, LastRejectedFrame = lastRejectedFrame, Stage = stage, PreOpenPolicyReady = policyReady, PriorSensexStateReady = priorReady,
                    ContractPreflight = coverage, ContractCoverageIssues = coverageIssues,
                    RequiredOptionMarkets = requiredOptionMarkets.ToArray(),
                    LastOptionPriceSourceByMarket = new Dictionary<string, DateTimeOffset>(optionPrices),
                    LastOptionDepthSourceByMarket = new Dictionary<string, DateTimeOffset>(optionDepth) };
        }
    }
}

public sealed record ResearchCaptureSnapshot(string Phase, DateTimeOffset? SubscribedAtUtc,
    int Instruments, bool GapObserved, IReadOnlyDictionary<string, DateTimeOffset> LastReceiptByMarket)
{
    private static readonly string[] Markets = ["NIFTY", "SENSEX", "BANKNIFTY"];
    public string? LastFailure { get; init; }
    public HistoricalFeedFrameRejection? LastRejectedFrame { get; init; }
    public string Stage { get; init; } = "Waiting";
    public bool PreOpenPolicyReady { get; init; }
    public bool PriorSensexStateReady { get; init; }
    public ReplayContractPreflight? ContractPreflight { get; init; }
    public IReadOnlyList<string> ContractCoverageIssues { get; init; } = [];
    public IReadOnlyList<string> RequiredOptionMarkets { get; init; } = [];
    public IReadOnlyDictionary<string, DateTimeOffset> LastOptionPriceSourceByMarket { get; init; } = new Dictionary<string, DateTimeOffset>();
    public IReadOnlyDictionary<string, DateTimeOffset> LastOptionDepthSourceByMarket { get; init; } = new Dictionary<string, DateTimeOffset>();
    public bool HasStalledMarket(DateTimeOffset now)
    {
        var india = TimeSpan.FromHours(5.5);
        var open = new DateTimeOffset(DateOnly.FromDateTime(now.ToOffset(india).DateTime)
            .ToDateTime(new TimeOnly(9, 15)), india);
        // A socket connected at 09:00 must not reconnect at 09:15:00 simply
        // because the first regular-session option/depth tick has not arrived.
        return SubscribedAtUtc is { } subscribed &&
        now - (subscribed > open ? subscribed : open) > TimeSpan.FromSeconds(30) &&
        (Markets.Any(market =>
            !LastReceiptByMarket.TryGetValue(market, out var last) || now - last > TimeSpan.FromSeconds(30)) ||
         RequiredOptionMarkets.Any(market =>
            !LastOptionPriceSourceByMarket.TryGetValue(market, out var price) || now - price > TimeSpan.FromSeconds(30) ||
            !LastOptionDepthSourceByMarket.TryGetValue(market, out var depth) || now - depth > TimeSpan.FromSeconds(30)));
    }
}
