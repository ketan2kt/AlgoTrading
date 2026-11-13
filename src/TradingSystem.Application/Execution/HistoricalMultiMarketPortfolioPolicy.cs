using System.Text.Json;

namespace TradingSystem.Application.Execution;

// Dispatch independent market rules on one chronological portfolio clock.
// Each rule receives its own market's signal/position history and an explicit
// all-market view for future correlated-exposure and shared-capital decisions.
public sealed class HistoricalMultiMarketPortfolioPolicy :
    IHistoricalReplayPortfolioPolicy
{
    private readonly (string Market, IHistoricalReplayPortfolioPolicy Policy)[] markets;

    public HistoricalMultiMarketPortfolioPolicy(
        IReadOnlyDictionary<string, IHistoricalReplayPortfolioPolicy> policies)
    {
        ArgumentNullException.ThrowIfNull(policies);
        if (policies.Count == 0 || policies.Any(value =>
                string.IsNullOrWhiteSpace(value.Key) || value.Value is null ||
                string.IsNullOrWhiteSpace(value.Value.CodeVersion)))
            throw new ArgumentException("Named replay market policies are required.",
                nameof(policies));
        markets = policies.Select(value => (value.Key.Trim().ToUpperInvariant(),
                value.Value)).OrderBy(value => value.Item1, StringComparer.Ordinal)
            .ToArray();
        if (markets.Select(value => value.Item1).Distinct(StringComparer.Ordinal)
                .Count() != markets.Length)
            throw new ArgumentException("Replay market keys must be unique.",
                nameof(policies));
    }

    public string CodeVersion => "shared-clock-v2|" + string.Join('|',
        markets.Select(value => value.Market + ":" + value.Policy.CodeVersion));

    public string ConfigurationManifest => JsonSerializer.Serialize(
        markets.Select(value => new
        {
            value.Market,
            value.Policy.CodeVersion,
            value.Policy.ConfigurationManifest
        }));

    // Preserve actual production scopes: Nifty book limits and cross-market
    // correlation evidence, not an invented aggregate capital gate.
    public bool ProductionPolicyParityVerified => markets.All(value => value.Policy.ProductionPolicyParityVerified);

    public IReadOnlyList<HistoricalReplayCandidate> Evaluate(
        HistoricalReplayPortfolioFrame frame) => markets.SelectMany(value =>
        {
            var decisions = value.Policy.Evaluate(Project(frame, value.Market));
            if (decisions.Any(item => !Owns(value.Market, item.Symbol)))
                throw new InvalidOperationException(
                    "A market replay policy emitted another market's contract.");
            return decisions;
        }).ToArray();

    public IReadOnlyList<HistoricalReplayStopUpdate> EvaluateStopUpdates(
        HistoricalReplayPortfolioFrame frame) => markets.SelectMany(value =>
        {
            var decisions = value.Policy.EvaluateStopUpdates(
                Project(frame, value.Market));
            if (decisions.Any(item => !Owns(value.Market, item.Symbol)))
                throw new InvalidOperationException(
                    "A market replay policy updated another market's stop.");
            return decisions;
        }).ToArray();

    public IReadOnlyList<HistoricalReplayPartialExit> EvaluatePartialExits(
        HistoricalReplayPortfolioFrame frame) => markets.SelectMany(value =>
        {
            var decisions = value.Policy.EvaluatePartialExits(
                Project(frame, value.Market));
            if (decisions.Any(item => !Owns(value.Market, item.Symbol)))
                throw new InvalidOperationException(
                    "A market replay policy reduced another market's position.");
            return decisions;
        }).ToArray();

    public IReadOnlyList<HistoricalReplayAddOn> EvaluateAddOns(
        HistoricalReplayPortfolioFrame frame) => markets.SelectMany(value =>
        {
            var decisions = value.Policy.EvaluateAddOns(
                Project(frame, value.Market));
            if (decisions.Any(item => !Owns(value.Market, item.Symbol)))
                throw new InvalidOperationException(
                    "A market replay policy added to another market's position.");
            return decisions;
        }).ToArray();

    public IReadOnlyList<HistoricalReplayExitRequest> EvaluateExits(
        HistoricalReplayPortfolioFrame frame) => markets.SelectMany(value =>
        {
            var decisions = value.Policy.EvaluateExits(
                Project(frame, value.Market));
            if (decisions.Any(item => !Owns(value.Market, item.Symbol)))
                throw new InvalidOperationException(
                    "A market replay policy exited another market's position.");
            return decisions;
        }).ToArray();

    public bool ShouldExtendTarget(HistoricalReplayPortfolioFrame frame,
        HistoricalReplayOpenPosition position)
    {
        var owner = markets.SingleOrDefault(value => Owns(value.Market,
            position.Symbol));
        return owner.Policy?.ShouldExtendTarget(Project(frame, owner.Market),
            position) ?? false;
    }

    public HistoricalReplayCandidate? AssessDeferredEntry(
        HistoricalReplayPortfolioFrame frame,
        HistoricalReplayCandidate pending,
        HistoricalReplayQuote optionQuote)
    {
        var owner = markets.SingleOrDefault(value => Owns(value.Market,
            pending.Symbol));
        if (owner.Policy is null)
            throw new InvalidOperationException(
                "Deferred entry has no market replay policy.");
        return owner.Policy.AssessDeferredEntry(Project(frame, owner.Market),
            pending, optionQuote);
    }

    private static bool Owns(string market, string symbol) =>
        symbol.StartsWith(market, StringComparison.Ordinal) ||
        symbol.StartsWith("NSE-" + market + "-", StringComparison.Ordinal) ||
        symbol.StartsWith("BSE-" + market + "-", StringComparison.Ordinal);

    private HistoricalReplayPortfolioFrame? cachedSourceFrame;
    private readonly Dictionary<string, HistoricalReplayPortfolioFrame> cachedProjections = new(StringComparer.Ordinal);
    private HistoricalReplayBar[]? cachedSourceBars;
    private readonly Dictionary<string, HistoricalReplayBar[]> cachedMarketBars = new(StringComparer.Ordinal);

    private HistoricalReplayPortfolioFrame Project(
        HistoricalReplayPortfolioFrame frame, string market)
    {
        if (!ReferenceEquals(cachedSourceFrame, frame))
        {
            cachedSourceFrame = frame;
            cachedProjections.Clear();
        }
        if (cachedProjections.TryGetValue(market, out var cached)) return cached;
        var ownOpen = frame.OpenPositions.Where(value => Owns(market, value.Symbol))
            .ToArray();
        var ownClosed = frame.ClosedTrades.Where(value => Owns(market, value.Symbol))
            .ToArray();
        var ownFills = frame.CompletedFills.Where(value => Owns(market, value.Symbol))
            .ToArray();
        var ownMarket = frame.Market with
        {
            CompletedBars = ProjectBars(frame.Market.CompletedBars, market),
            LatestQuotes = frame.Market.LatestQuotes.Where(value =>
                    Owns(market, value.Key)).ToDictionary(value => value.Key,
                    value => value.Value, StringComparer.Ordinal)
        };
        var projected = frame with
        {
            Market = ownMarket,
            OpenPosition = ownOpen.FirstOrDefault(),
            OpenPositions = ownOpen,
            ClosedTrades = ownClosed,
            CompletedFills = ownFills,
            PortfolioOpenPositions = frame.PortfolioOpenPositions.Count > 0
                ? frame.PortfolioOpenPositions : frame.OpenPositions,
            PortfolioClosedTrades = frame.PortfolioClosedTrades.Count > 0
                ? frame.PortfolioClosedTrades : frame.ClosedTrades,
            LatestOptionChain = frame.LatestOptionChainsByUnderlying
                .GetValueOrDefault(market),
            PreviousOptionChain = frame.PreviousOptionChainsByUnderlying
                .GetValueOrDefault(market)
        };
        cachedProjections.Add(market, projected);
        return projected;
    }

    private HistoricalReplayBar[] ProjectBars(IReadOnlyList<HistoricalReplayBar> bars, string market)
    {
        // The runner publishes a new immutable array on each completed bar.
        // Keep that identity in each market projection so the archived JSON
        // adapter can reuse the exact encoded bar history on subsequent ticks.
        if (bars is not HistoricalReplayBar[] array)
            return bars.Where(value => Owns(market, value.Symbol)).ToArray();
        if (!ReferenceEquals(cachedSourceBars, array))
        {
            cachedSourceBars = array;
            cachedMarketBars.Clear();
        }
        if (!cachedMarketBars.TryGetValue(market, out var projected))
            cachedMarketBars.Add(market, projected = array.Where(value => Owns(market, value.Symbol)).ToArray());
        return projected;
    }
}
