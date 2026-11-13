using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Execution;

public sealed record HistoricalReplayOpenPosition(string Symbol, string Strategy,
    DateTimeOffset EntryAtUtc, decimal EntryPrice, decimal LatestPrice,
    int Quantity, decimal StopPrice, decimal TargetPrice)
{
    public int OriginalQuantity { get; init; }
    public decimal InitialStopPrice { get; init; }
    public decimal InitialTargetPrice { get; init; }
    public Direction? UnderlyingDirection { get; init; }
    public MarketRegime? SignalRegime { get; init; }
    public DateTimeOffset DecisionAtUtc { get; init; }
    public DateOnly? ContractExpiryDate { get; init; }
    public decimal SignalConfidence { get; init; }
    public int? LotSize { get; init; }
    public int AddOnQuantity { get; init; }
    public int BaseQuantity { get; init; }
    public decimal HighestObservedPrice { get; init; }
    public decimal HighestDecisionPrice { get; init; }
}

public sealed record HistoricalReplayPortfolioFrame(HistoricalReplayFrame Market,
    HistoricalReplayOpenPosition? OpenPosition,
    IReadOnlyList<HistoricalReplayFill> ClosedTrades)
{
    public HistoricalReplayControlState Controls { get; init; } = HistoricalReplayControlState.Empty;
    // OpenPosition is the legacy first-position view. New portfolio-aware
    // policies must inspect the complete, symbol-ordered collection.
    public IReadOnlyList<HistoricalReplayOpenPosition> OpenPositions { get; init; } = [];
    public IReadOnlyList<HistoricalReplayFill> CompletedFills { get; init; } = [];
    // Market-specific policy projections retain the all-market portfolio for
    // shared-capital and correlated-exposure checks.
    public IReadOnlyList<HistoricalReplayOpenPosition> PortfolioOpenPositions
        { get; init; } = [];
    public IReadOnlyList<HistoricalReplayFill> PortfolioClosedTrades
        { get; init; } = [];
    public HistoricalOptionChainFrame? LatestOptionChain { get; init; }
    public HistoricalOptionChainFrame? PreviousOptionChain { get; init; }
    public IReadOnlyDictionary<string, HistoricalOptionChainFrame>
        LatestOptionChainsByUnderlying { get; init; } =
            new Dictionary<string, HistoricalOptionChainFrame>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, HistoricalOptionChainFrame>
        PreviousOptionChainsByUnderlying { get; init; } =
            new Dictionary<string, HistoricalOptionChainFrame>(StringComparer.Ordinal);
}

public sealed record HistoricalOptionChainFrame(DateTimeOffset AvailableAtUtc,
    DateTimeOffset SourceTimestampUtc, DateOnly ExpiryDate,
    IReadOnlyList<OptionOpenInterestStrike> Strikes)
{
    public string UnderlyingSymbol { get; init; } = string.Empty;
}

public sealed record HistoricalReplayStopUpdate(string Symbol,
    DateTimeOffset DecidedAtUtc, decimal NewStopPrice)
{
    public bool RecomputedFromInitialRisk { get; init; }
}

public sealed record HistoricalReplayPartialExit(string Symbol,
    DateTimeOffset DecidedAtUtc, int Quantity, string Reason)
{
    // Paper can request odd option units. A broker cannot execute them as a
    // complete lot, so the historical outcome must not be certified.
    public bool NonExecutablePaperQuantity { get; init; }
}

// A same-contract winner add-on is a separate buy order, admitted from an
// as-of frame and priced only by a subsequent fresh option quote.
public sealed record HistoricalReplayAddOn(string Symbol,
    DateTimeOffset DecidedAtUtc, int Quantity,
    decimal MaximumAdditionalRisk, string Reason);

// A voluntary policy exit is decided only from the as-of frame. The runner
// models its fill on a subsequent quote, never at the decision's own mark.
public sealed record HistoricalReplayExitRequest(string Symbol,
    DateTimeOffset DecidedAtUtc, string Reason);

// Unlike a candidate-only policy, this policy sees earlier *simulated* fills.
// It can therefore apply the same cooldown, re-entry and position state to
// both versions being compared, instead of reading the original run's trades.
public interface IHistoricalReplayPortfolioPolicy
{
    string CodeVersion { get; }
    // An adapter that shares only selected production gates must never be
    // treated as a full-policy replay, even when its market data is complete.
    bool ProductionPolicyParityVerified => false;
    // Canonical non-secret settings/contract-selection manifest. A code version
    // alone cannot distinguish two runs with different policy configuration.
    string ConfigurationManifest => string.Empty;
    IReadOnlyList<HistoricalReplayCandidate> Evaluate(HistoricalReplayPortfolioFrame frame);
    // Optional as-of stop decisions. The offline runner never loosens a stop
    // and requires a later quote to fill an exit triggered by this update.
    IReadOnlyList<HistoricalReplayStopUpdate> EvaluateStopUpdates(
        HistoricalReplayPortfolioFrame frame) => [];
    // Research-only reduction request, filled at the next fresh quote. The
    // remaining long option retains its protective stop and time deadline.
    IReadOnlyList<HistoricalReplayPartialExit> EvaluatePartialExits(
        HistoricalReplayPortfolioFrame frame) => [];
    IReadOnlyList<HistoricalReplayAddOn> EvaluateAddOns(
        HistoricalReplayPortfolioFrame frame) => [];
    IReadOnlyList<HistoricalReplayExitRequest> EvaluateExits(
        HistoricalReplayPortfolioFrame frame) => [];
    // Production may keep a confirmed directional runner beyond its original
    // target. This decision is made from the same as-of evidence as an exit.
    bool ShouldExtendTarget(HistoricalReplayPortfolioFrame frame,
        HistoricalReplayOpenPosition position) => false;
    HistoricalReplayCandidate? AssessDeferredEntry(
        HistoricalReplayPortfolioFrame frame,
        HistoricalReplayCandidate pending,
        HistoricalReplayQuote optionQuote) =>
        throw new InvalidOperationException(
            "The replay policy does not implement deferred option-quote admission.");
}

public static class HistoricalPortfolioReplay
{
    public const string EngineVersion = "portfolio-replay-v12";
    private static readonly TimeSpan MaximumQuoteAge = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan MaximumBookAge = TimeSpan.FromSeconds(30);

    public static HistoricalReplayResult Run(
        IReadOnlyList<HistoricalReplayBar> sourceBars,
        IReadOnlyList<HistoricalReplayQuote> sourceQuotes,
        IHistoricalReplayPortfolioPolicy policy,
        HistoricalReplayParameters parameters,
        DateTimeOffset sessionStartUtc,
        DateTimeOffset sessionEndUtc,
        IReadOnlyList<HistoricalOptionChainFrame>? sourceChains = null,
        string exchangeCode = "NSE",
        IReadOnlyDictionary<string, string>? contractExchanges = null,
        IReadOnlyList<HistoricalReplayControlEvent>? sourceControls = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceBars);
        ArgumentNullException.ThrowIfNull(sourceQuotes);
        ArgumentNullException.ThrowIfNull(policy);
        parameters.Validate();
        if (exchangeCode is not ("NSE" or "BSE"))
            throw new ArgumentOutOfRangeException(nameof(exchangeCode));
        if (contractExchanges is not null && contractExchanges.Any(value =>
                string.IsNullOrWhiteSpace(value.Key) ||
                value.Value is not ("NSE" or "BSE")))
            throw new ArgumentException(
                "Every replay contract needs a supported exchange.",
                nameof(contractExchanges));
        string ExchangeFor(string symbol) => contractExchanges is null
            ? exchangeCode
            : contractExchanges.TryGetValue(symbol, out var value)
                ? value : throw new InvalidOperationException(
                    $"No exchange was supplied for replay contract {symbol}.");
        if (string.IsNullOrWhiteSpace(policy.CodeVersion) ||
            sessionStartUtc >= sessionEndUtc)
            throw new ArgumentException("A versioned policy and valid session are required.");
        ValidateInputs(sourceBars, sourceQuotes, sourceChains ?? [], cancellationToken);
        var controls = HistoricalReplayControls.Validate(sourceControls ?? []);
        var consumedBook = new Dictionary<(string Symbol, bool Buy), (DateTimeOffset At, long Units)>();
        bool CanFill(HistoricalReplayQuote quote, int units, bool buy)
        {
            if (!parameters.RequireDisplayedFillSize) return true;
            if (quote.Source != "GrowwFeedSample" || !quote.HasExecutableBook() ||
                quote.DepthSourceAtUtc is not { } depthAt || depthAt > quote.AvailableAtUtc ||
                quote.AvailableAtUtc - depthAt > MaximumBookAge) return false;
            var consumed = consumedBook.GetValueOrDefault((quote.Symbol, buy));
            return depthAt >= consumed.At &&
                (buy ? quote.OfferQuantity : quote.BidQuantity) - (depthAt == consumed.At ? consumed.Units : 0) >= units;
        }
        void Consume(HistoricalReplayQuote quote, int units, bool buy)
        {
            if (!parameters.RequireDisplayedFillSize || quote.DepthSourceAtUtc is not { } depthAt) return;
            var key = (quote.Symbol, buy);
            var prior = consumedBook.GetValueOrDefault(key);
            consumedBook[key] = (depthAt, (prior.At == depthAt ? prior.Units : 0) + units);
        }

        var bars = sourceBars.Where(value => value.IntervalSeconds > 0 &&
                value.AvailableAtUtc <= sessionEndUtc)
            .OrderBy(value => value.AvailableAtUtc)
            .ThenBy(value => value.Symbol, StringComparer.Ordinal)
            .ThenBy(value => value.OpenTimeUtc)
            .ThenBy(value => value.IntervalSeconds).ToArray();
        var quotes = HistoricalQuotePaths.Ordered(sourceQuotes).Where(value => value.LastPrice > 0m &&
                value.AvailableAtUtc <= sessionEndUtc);
        using var quoteCursor = quotes.GetEnumerator();
        var hasQuote = quoteCursor.MoveNext();
        var chains = (sourceChains ?? []).Where(value =>
                value.AvailableAtUtc <= sessionEndUtc)
            .OrderBy(value => value.AvailableAtUtc).ToArray();
        var times = bars.Select(value => value.AvailableAtUtc)
            .Concat(chains.Select(value => value.AvailableAtUtc))
            .Concat(controls.Where(value => value.AvailableAtUtc <= sessionEndUtc).Select(value => value.AvailableAtUtc))
            .Distinct().OrderBy(value => value).ToArray();
        var completedBars = new List<HistoricalReplayBar>();
        var latest = new Dictionary<string, HistoricalReplayQuote>(StringComparer.Ordinal);
        var closed = new List<HistoricalReplayFill>();
        var pendingEntries = new Dictionary<string, HistoricalReplayCandidate>(
            StringComparer.Ordinal);
        var pendingPartials = new Dictionary<string, HistoricalReplayPartialExit>(
            StringComparer.Ordinal);
        var pendingAddOns = new Dictionary<string, HistoricalReplayAddOn>(
            StringComparer.Ordinal);
        var openPositions = new Dictionary<string, Position>(StringComparer.Ordinal);
        DateTimeOffset? lastEntryAt = null;
        var candidateCount = 0;
        var missingEntry = 0;
        var missingAddOn = 0;
        var unsupportedPartials = new HashSet<string>(StringComparer.Ordinal);
        var missingExit = 0;
        var stale = 0;
        DateTimeOffset? firstUncertainty = null;
        void FlagUncertainty(DateTimeOffset at) =>
            firstUncertainty = firstUncertainty is not { } previous || at < previous ? at : previous;
        var barIndex = 0;
        var quoteIndex = 0;
        var chainIndex = 0;
        decimal equityPeak = 0m;
        decimal markToMarketDrawdown = 0m;
        decimal peakEntryCapitalExposure = 0m;
        var peakConcurrentPositions = 0;

        HistoricalReplayPortfolioFrame Snapshot(DateTimeOffset atUtc)
        {
            var visibleQuotes = latest.Where(value =>
                    value.Value.IsFreshAsOf(atUtc, MaximumQuoteAge))
                .ToDictionary(value => value.Key, value => value.Value,
                    StringComparer.Ordinal);
            var market = new HistoricalReplayFrame(atUtc,
                completedBars.ToArray(), visibleQuotes);
            var (visibleChain, priorChain) = VisibleChains(chains,
                chainIndex, atUtc);
            var latestByUnderlying = new Dictionary<string,
                HistoricalOptionChainFrame>(StringComparer.Ordinal);
            var previousByUnderlying = new Dictionary<string,
                HistoricalOptionChainFrame>(StringComparer.Ordinal);
            foreach (var underlying in chains.Take(chainIndex)
                         .Select(value => value.UnderlyingSymbol)
                         .Where(value => value.Length > 0)
                         .Distinct(StringComparer.Ordinal))
            {
                var (marketLatest, marketPrevious) = VisibleChains(chains,
                    chainIndex, atUtc, underlying);
                if (marketLatest is not null)
                    latestByUnderlying.Add(underlying, marketLatest);
                if (marketPrevious is not null)
                    previousByUnderlying.Add(underlying, marketPrevious);
            }
            var positionViews = openPositions.Values.OrderBy(value =>
                value.Candidate.Symbol, StringComparer.Ordinal).Select(value =>
                new HistoricalReplayOpenPosition(value.Candidate.Symbol,
                    value.Candidate.Strategy, value.EntryAtUtc,
                    value.EntryPrice, value.LatestPrice,
                    value.RemainingQuantity, value.StopPrice,
                    value.TargetPrice)
                {
                    OriginalQuantity = value.TotalQuantity,
                    InitialStopPrice = value.InitialStopPrice,
                    InitialTargetPrice = value.TargetPrice,
                    UnderlyingDirection = value.Candidate.UnderlyingDirection,
                    SignalRegime = value.Candidate.SignalRegime,
                    DecisionAtUtc = value.Candidate.StrategyDecisionAtUtc ??
                        value.Candidate.DecidedAtUtc,
                    ContractExpiryDate = value.Candidate.ContractExpiryDate,
                    SignalConfidence = value.Candidate.Confidence,
                    LotSize = value.Candidate.LotSize,
                    AddOnQuantity = value.AddOnQuantity,
                    BaseQuantity = value.Candidate.Quantity,
                    HighestObservedPrice = value.HighestObservedPrice,
                    HighestDecisionPrice = value.HighestDecisionPrice
                }).ToArray();
            var closedPositions = HistoricalReplayTradeAggregation
                .CompletedPositions(closed);
            return new HistoricalReplayPortfolioFrame(market,
                positionViews.FirstOrDefault(), closedPositions)
            {
                OpenPositions = positionViews,
                Controls = HistoricalReplayControls.At(controls, atUtc),
                CompletedFills = closed.ToArray(),
                PortfolioOpenPositions = positionViews,
                PortfolioClosedTrades = closedPositions,
                // Legacy single-market views are deliberately absent when
                // multiple underlyings are visible; a composite policy must
                // select its own market-keyed OI frame.
                LatestOptionChain = latestByUnderlying.Count > 1 ? null :
                    latestByUnderlying.Count == 1
                        ? latestByUnderlying.Values.Single() : visibleChain,
                PreviousOptionChain = latestByUnderlying.Count > 1 ? null :
                    latestByUnderlying.Count == 1
                        ? previousByUnderlying.GetValueOrDefault(
                            latestByUnderlying.Keys.Single()) : priorChain,
                LatestOptionChainsByUnderlying = latestByUnderlying,
                PreviousOptionChainsByUnderlying = previousByUnderlying
            };
        }

        void ApplyStopUpdates(HistoricalReplayPortfolioFrame snapshot, bool recomputeBeforeQuotes = false)
        {
            var atUtc = snapshot.Market.AvailableAtUtc;
            var stopUpdates = policy.EvaluateStopUpdates(snapshot);
            if (stopUpdates.Select(value => value.Symbol).Distinct(
                    StringComparer.Ordinal).Count() != stopUpdates.Count ||
                stopUpdates.Any(value => value.DecidedAtUtc != atUtc ||
                    value.NewStopPrice <= 0m ||
                    !openPositions.ContainsKey(value.Symbol)))
                throw new InvalidOperationException(
                    "A replay policy emitted an invalid stop update.");
            foreach (var update in stopUpdates)
            {
                var position = openPositions[update.Symbol];
                if (recomputeBeforeQuotes && !update.RecomputedFromInitialRisk) continue;
                if (update.RecomputedFromInitialRisk && (!position.Candidate.RecomputesProtectionFromInitialRisk ||
                        update.NewStopPrice < position.InitialStopPrice))
                    throw new InvalidOperationException("Recomputed protection must preserve the original initial-risk boundary.");
                if (!position.Unpriced &&
                    atUtc - position.LastQuoteAtUtc > MaximumQuoteAge)
                {
                    position.Unpriced = true;
                    stale++;
                    FlagUncertainty(position.EntryAtUtc);
                }
                if (update.NewStopPrice < position.StopPrice && !update.RecomputedFromInitialRisk)
                    throw new InvalidOperationException(
                        "A replay policy cannot loosen a protective stop.");
                if (position.ExitTriggeredAtUtc is not null ||
                    update.NewStopPrice == position.StopPrice ||
                    position.Unpriced) continue;
                position.StopPrice = update.NewStopPrice;
                if (recomputeBeforeQuotes) continue; // This timestamp's quote tests the recomputed stop below.
                var decisionPrice = position.Candidate.TriggerOnLastPrice &&
                    latest.TryGetValue(update.Symbol, out var decisionQuote)
                    ? decisionQuote.LastPrice : position.LatestPrice;
                if (decisionPrice <= update.NewStopPrice)
                {
                    position.ExitTriggeredAtUtc = atUtc;
                    position.ExitReason = "PolicyStop";
                    pendingAddOns.Remove(update.Symbol);
                }
            }
        }

        void QueuePartialExits(HistoricalReplayPortfolioFrame snapshot,
            IReadOnlySet<string> filledAddOnsThisTime)
        {
            var atUtc = snapshot.Market.AvailableAtUtc;
            var partials = policy.EvaluatePartialExits(snapshot);
            if (partials.Select(value => value.Symbol).Distinct(
                    StringComparer.Ordinal).Count() != partials.Count)
                throw new InvalidOperationException(
                    "A replay policy requested duplicate partial exits.");
            foreach (var partial in partials)
            {
                // Production processes a qualified winner add-on before its
                // partial-profit branch. The deferred replay order cannot
                // also reduce the position while that buy is pending.
                if (pendingAddOns.ContainsKey(partial.Symbol) ||
                    filledAddOnsThisTime.Contains(partial.Symbol)) continue;
                // A stop/full exit may already have been queued from the earlier
                // as-of snapshot in this timestamp. It supersedes a newly eligible
                // partial; later evidence cannot undo the full-exit decision.
                if (openPositions.TryGetValue(partial.Symbol, out var exiting) &&
                    exiting.ExitTriggeredAtUtc is not null) continue;
                if (partial.NonExecutablePaperQuantity)
                {
                    if (!openPositions.TryGetValue(partial.Symbol, out var unsupportedPosition) ||
                        partial.DecidedAtUtc != atUtc)
                        throw new InvalidOperationException(
                            "A replay policy reported an invalid paper-only partial.");
                    unsupportedPartials.Add(partial.Symbol);
                    FlagUncertainty(unsupportedPosition.EntryAtUtc);
                    continue;
                }
                // A new underlying observation or bar may re-evaluate the same
                // desired partial before the next executable option quote. Keep
                // the original queued order/time; do not submit it twice.
                if (pendingPartials.TryGetValue(partial.Symbol, out var pendingPartial) &&
                    partial.DecidedAtUtc == atUtc && (parameters.RequireDisplayedFillSize ||
                    partial.Quantity == pendingPartial.Quantity && partial.Reason == pendingPartial.Reason)) continue;
                if (partial.DecidedAtUtc != atUtc ||
                    string.IsNullOrWhiteSpace(partial.Reason) ||
                    !openPositions.TryGetValue(partial.Symbol, out var position) ||
                    partial.Quantity <= 0 ||
                    partial.Quantity >= position.RemainingQuantity ||
                    position.Candidate.LotSize is not > 0 ||
                    partial.Quantity % position.Candidate.LotSize.Value != 0 ||
                    position.RemainingQuantity % position.Candidate.LotSize.Value != 0 ||
                    position.Unpriced || position.ExitTriggeredAtUtc is not null ||
                    pendingPartials.ContainsKey(partial.Symbol) ||
                    !snapshot.Market.LatestQuotes.TryGetValue(partial.Symbol,
                        out var quote) || quote.AvailableAtUtc < position.EntryAtUtc)
                    throw new InvalidOperationException(
                        "A replay policy requested an invalid or unprotected partial exit.");
                pendingPartials.Add(partial.Symbol, partial);
            }
        }

        void QueueAddOns(HistoricalReplayPortfolioFrame snapshot)
        {
            var atUtc = snapshot.Market.AvailableAtUtc;
            var addOns = policy.EvaluateAddOns(snapshot);
            if (addOns.Select(value => value.Symbol).Distinct(
                    StringComparer.Ordinal).Count() != addOns.Count)
                throw new InvalidOperationException(
                    "A replay policy requested duplicate winner add-ons.");
            foreach (var addOn in addOns)
            {
                if (pendingAddOns.ContainsKey(addOn.Symbol)) continue;
                if (addOn.DecidedAtUtc != atUtc ||
                    string.IsNullOrWhiteSpace(addOn.Reason) ||
                    !openPositions.TryGetValue(addOn.Symbol, out var position) ||
                    position.AddOnQuantity > 0 || addOn.Quantity <= 0 ||
                    addOn.MaximumAdditionalRisk < 0m ||
                    position.Candidate.LotSize is not > 0 ||
                    addOn.Quantity % position.Candidate.LotSize.Value != 0 ||
                    position.RemainingQuantity != position.TotalQuantity ||
                    position.Unpriced || position.ExitTriggeredAtUtc is not null ||
                    pendingPartials.ContainsKey(addOn.Symbol) ||
                    !snapshot.Market.LatestQuotes.TryGetValue(addOn.Symbol,
                        out var quote) || quote.AvailableAtUtc < position.EntryAtUtc)
                    throw new InvalidOperationException(
                        "A replay policy requested an invalid or unprotected add-on.");
                pendingAddOns.Add(addOn.Symbol, addOn);
            }
        }

        void QueuePolicyExits(HistoricalReplayPortfolioFrame snapshot)
        {
            var atUtc = snapshot.Market.AvailableAtUtc;
            var exits = policy.EvaluateExits(snapshot);
            if (exits.Select(value => value.Symbol).Distinct(
                    StringComparer.Ordinal).Count() != exits.Count)
                throw new InvalidOperationException(
                    "A replay policy requested duplicate position exits.");
            foreach (var exit in exits)
            {
                if (exit.DecidedAtUtc != atUtc ||
                    string.IsNullOrWhiteSpace(exit.Reason) ||
                    !openPositions.TryGetValue(exit.Symbol, out var position) ||
                    position.Unpriced ||
                    !snapshot.Market.LatestQuotes.TryGetValue(exit.Symbol,
                        out var quote) || quote.AvailableAtUtc < position.EntryAtUtc)
                    throw new InvalidOperationException(
                        "A replay policy requested an invalid or unpriced position exit.");
                // An observed stop/target has precedence over a policy's
                // simultaneous voluntary-exit request.
                if (position.ExitTriggeredAtUtc is not null)
                {
                    if (position.ExitTriggeredAtUtc == atUtc && exit.Reason == "ManualExit")
                        position.ExitReason = exit.Reason;
                    continue;
                }
                position.ExitTriggeredAtUtc = atUtc;
                position.ExitReason = exit.Reason;
                pendingPartials.Remove(exit.Symbol);
                pendingAddOns.Remove(exit.Symbol);
            }
        }

        var timeIndex = 0;
        while (timeIndex < times.Length || hasQuote)
        {
            var time = !hasQuote || timeIndex < times.Length && times[timeIndex] <= quoteCursor.Current.AvailableAtUtc
                ? times[timeIndex] : quoteCursor.Current.AvailableAtUtc;
            while (timeIndex < times.Length && times[timeIndex] == time) timeIndex++;
            cancellationToken.ThrowIfCancellationRequested();
            var controlState = HistoricalReplayControls.At(controls, time);
            if (controlState.BlocksNiftyEntries)
            {
                foreach (var symbol in pendingEntries.Keys.Where(value => value.StartsWith("NIFTY", StringComparison.Ordinal)).ToArray())
                    pendingEntries.Remove(symbol);
                foreach (var symbol in pendingAddOns.Keys.Where(value => value.StartsWith("NIFTY", StringComparison.Ordinal)).ToArray())
                    pendingAddOns.Remove(symbol);
            }
            if (time >= sessionStartUtc && time < sessionEndUtc && controls.Any(value => value.AvailableAtUtc == time))
                QueuePolicyExits(Snapshot(time));
            var barTargets = new HashSet<string>(StringComparer.Ordinal);
            var quoteTargets = new HashSet<string>(StringComparer.Ordinal);
            var quoteAdmissions = new HashSet<string>(StringComparer.Ordinal);
            var filledAddOnsThisTime = new HashSet<string>(StringComparer.Ordinal);
            while (chainIndex < chains.Length && chains[chainIndex].AvailableAtUtc == time)
                chainIndex++;
            var firstBarIndex = barIndex;
            while (barIndex < bars.Length && bars[barIndex].AvailableAtUtc == time)
            {
                var bar = bars[barIndex++];
                completedBars.Add(bar);
                if (parameters.RequireDisplayedFillSize ||
                    !openPositions.TryGetValue(bar.Symbol, out var open) ||
                    open.Unpriced || open.ExitTriggeredAtUtc is not null ||
                    bar.IntervalSeconds != 60 ||
                    bar.OpenTimeUtc < open.EntryAtUtc ||
                    bar.AvailableAtUtc > open.DeadlineUtc) continue;
                // The minute's high/low ordering is unknowable. If both
                // barriers were touched, trigger the adverse stop first.
                if (bar.Low <= open.StopPrice)
                {
                    open.ExitTriggeredAtUtc = time;
                    open.ExitReason = bar.High >= open.TargetPrice
                        ? "AmbiguousIntrabarStop" : "ObservedBarStop";
                    pendingAddOns.Remove(bar.Symbol);
                }
                else if (bar.High >= open.TargetPrice)
                    barTargets.Add(bar.Symbol);
            }
            foreach (var pending in pendingEntries.Values.ToArray())
            {
                if (time - pending.DecidedAtUtc <= MaximumQuoteAge) continue;
                pendingEntries.Remove(pending.Symbol);
                missingEntry++;
                stale++;
                FlagUncertainty(pending.StrategyDecisionAtUtc ?? pending.DecidedAtUtc);
            }
            foreach (var pending in pendingAddOns.Values.ToArray())
            {
                if (time - pending.DecidedAtUtc <= MaximumQuoteAge) continue;
                pendingAddOns.Remove(pending.Symbol);
                missingAddOn++;
                stale++;
                FlagUncertainty(openPositions.TryGetValue(pending.Symbol, out var unresolved) ? unresolved.EntryAtUtc : pending.DecidedAtUtc);
            }
            if (parameters.RequireDisplayedFillSize)
            {
                foreach (var pending in pendingPartials.Values.ToArray())
                {
                    if (time - pending.DecidedAtUtc <= MaximumQuoteAge) continue;
                    pendingPartials.Remove(pending.Symbol);
                    unsupportedPartials.Add(pending.Symbol);
                    if (openPositions.TryGetValue(pending.Symbol, out var unresolved))
                        FlagUncertainty(unresolved.EntryAtUtc);
                }
            }
            if (barIndex != firstBarIndex && time >= sessionStartUtc &&
                time < sessionEndUtc)
            {
                // A completed bar and quote with the same receipt timestamp
                // have unknown ordering. Evaluate before applying those quotes
                // so neither open-position marks nor closed fills leak forward.
                var snapshot = Snapshot(time);
                foreach (var symbol in barTargets)
                {
                    if (!openPositions.TryGetValue(symbol, out var open) ||
                        open.ExitTriggeredAtUtc is not null) continue;
                    var view = snapshot.OpenPositions.Single(value =>
                        value.Symbol == symbol);
                    if (policy.ShouldExtendTarget(snapshot, view)) continue;
                    open.ExitTriggeredAtUtc = time;
                    open.ExitReason = "ObservedBarTarget";
                    pendingAddOns.Remove(symbol);
                }
                var decisions = policy.Evaluate(snapshot).Where(value =>
                    !controlState.BlocksNiftyEntries || !value.Symbol.StartsWith("NIFTY", StringComparison.Ordinal)).ToArray();
                if (decisions.Any(value => value.DecidedAtUtc != time))
                    throw new InvalidOperationException(
                        "A replay policy emitted an off-frame decision.");
                ApplyStopUpdates(snapshot);
                QueuePolicyExits(Snapshot(time));
                candidateCount += decisions.Length;
                if (decisions.Length > 0 && (lastEntryAt is null ||
                    time - lastEntryAt.Value >= TimeSpan.FromMinutes(
                        parameters.MinimumMinutesBetweenEntries)))
                {
                    // Reserve capacity before any later quote arrives. Do not
                    // rank competing proposals with hindsight or stack a symbol.
                    var remaining = parameters.MaximumConcurrentPositions -
                        openPositions.Count - pendingEntries.Count;
                    if (decisions.Length <= remaining && !decisions.Any(value =>
                            value.Quantity <= 0 ||
                            string.IsNullOrWhiteSpace(value.Symbol) ||
                            openPositions.ContainsKey(value.Symbol) ||
                            pendingEntries.ContainsKey(value.Symbol)) &&
                        decisions.Select(value => value.Symbol).Distinct(
                            StringComparer.Ordinal).Count() == decisions.Length)
                        foreach (var decision in decisions.OrderBy(value =>
                            value.Symbol, StringComparer.Ordinal))
                            pendingEntries.Add(decision.Symbol, decision);
                }
            }
            var firstQuoteIndex = quoteIndex;
            var quoteBatch = new List<HistoricalReplayQuote>();
            while (hasQuote && quoteCursor.Current.AvailableAtUtc == time)
            {
                var quote = quoteCursor.Current;
                hasQuote = quoteCursor.MoveNext();
                quoteIndex++;
                // Require an as-of receipt and reject future source timestamps;
                // last-trade age is not itself the age of a current order book.
                if (!quote.IsFreshAsOf(time, MaximumQuoteAge)) continue;
                latest[quote.Symbol] = quote;
                quoteBatch.Add(quote);
            }
            if (quoteBatch.Count > 0 && openPositions.Values.Any(value => value.Candidate.RecomputesProtectionFromInitialRisk))
                ApplyStopUpdates(Snapshot(time), recomputeBeforeQuotes: true);
            foreach (var quote in quoteBatch)
            {
                // A recorded withdrawn/one-sided book must invalidate the latest
                // liquidity, not silently fill at LTP. Keep it in the as-of frame.
                if (quote.Source == "GrowwFeedSample" && !quote.HasExecutableBook() &&
                    !parameters.RequireDisplayedFillSize)
                {
                    if (openPositions.TryGetValue(quote.Symbol, out var illiquid)) illiquid.Unpriced = true;
                    continue;
                }
                if (openPositions.TryGetValue(quote.Symbol, out var open))
                {
                    if (quote.AvailableAtUtc - open.LastQuoteAtUtc > MaximumQuoteAge)
                    {
                        open.Unpriced = true;
                        stale++;
                        FlagUncertainty(open.EntryAtUtc);
                    }
                    else if (!open.Unpriced)
                    {
                        var liquidationMark = SellReferencePrice(quote);
                        var canMark = !parameters.RequireDisplayedFillSize || quote.HasExecutableBook();
                        var decisionMark = open.Candidate.TriggerOnLastPrice
                            ? quote.LastPrice : liquidationMark;
                        if (canMark)
                        {
                            open.HighestObservedPrice = Math.Max(open.HighestObservedPrice, liquidationMark);
                            open.LowestObservedPrice = Math.Min(open.LowestObservedPrice, liquidationMark);
                        }
                        if (canMark || open.Candidate.TriggerOnLastPrice)
                            open.HighestDecisionPrice = Math.Max(open.HighestDecisionPrice, decisionMark);
                        // A queued reduction cannot fill through a protective
                        // stop touched by this same quote. Its internal order
                        // relative to that stop is unknown, so take the
                        // adverse event first and await a later exit quote.
                        if (open.ExitTriggeredAtUtc is null &&
                            quote.AvailableAtUtc > open.EntryAtUtc &&
                            quote.AvailableAtUtc <= open.DeadlineUtc &&
                            (canMark || open.Candidate.TriggerOnLastPrice) && decisionMark <= open.StopPrice)
                        {
                            open.ExitTriggeredAtUtc = quote.AvailableAtUtc;
                            open.ExitReason = "ObservedStop";
                            pendingPartials.Remove(quote.Symbol);
                            pendingAddOns.Remove(quote.Symbol);
                        }
                        if (pendingAddOns.TryGetValue(quote.Symbol,
                                out var addOn) &&
                            quote.AvailableAtUtc > addOn.DecidedAtUtc &&
                            CanFill(quote, addOn.Quantity, buy: true))
                        {
                            pendingAddOns.Remove(quote.Symbol);
                            var addOnPrice = BuyReferencePrice(quote) *
                                (1m + parameters.EntrySlippagePercent);
                            if (open.ExitTriggeredAtUtc is null &&
                                quote.AvailableAtUtc <= open.DeadlineUtc &&
                                quote.AvailableAtUtc - addOn.DecidedAtUtc <=
                                MaximumQuoteAge &&
                                addOnPrice > open.StopPrice &&
                                (addOnPrice - open.StopPrice) * addOn.Quantity <=
                                addOn.MaximumAdditionalRisk)
                            {
                                open.AddOnQuantity = addOn.Quantity;
                                open.AddOnPrice = addOnPrice;
                                open.AddOnAt = quote.AvailableAtUtc;
                                open.RemainingQuantity += addOn.Quantity;
                                Consume(quote, addOn.Quantity, buy: true);
                                filledAddOnsThisTime.Add(quote.Symbol);
                                peakEntryCapitalExposure = Math.Max(
                                    peakEntryCapitalExposure,
                                    openPositions.Values.Sum(value =>
                                        value.EntryPrice * value.RemainingQuantity));
                            }
                            else if (open.ExitTriggeredAtUtc is null)
                            {
                                missingAddOn++;
                                FlagUncertainty(open.EntryAtUtc);
                            }
                        }
                        if (pendingPartials.TryGetValue(quote.Symbol,
                                out var partial) &&
                            quote.AvailableAtUtc > partial.DecidedAtUtc &&
                            CanFill(quote, partial.Quantity, buy: false))
                        {
                            pendingPartials.Remove(quote.Symbol);
                            if (open.ExitTriggeredAtUtc is null &&
                                quote.AvailableAtUtc <= open.DeadlineUtc &&
                                quote.AvailableAtUtc - partial.DecidedAtUtc <=
                                MaximumQuoteAge)
                            {
                                closed.Add(Close(open, quote, parameters,
                                    partial.Quantity, partial.Reason));
                                Consume(quote, partial.Quantity, buy: false);
                                open.RemainingQuantity -= partial.Quantity;
                            }
                            else if (open.ExitTriggeredAtUtc is null &&
                                     quote.AvailableAtUtc <= open.DeadlineUtc)
                            {
                                open.Unpriced = true;
                                stale++;
                                FlagUncertainty(open.EntryAtUtc);
                            }
                        }
                        if (!open.Unpriced && open.ExitTriggeredAtUtc is not null)
                        {
                            if (quote.AvailableAtUtc > open.ExitTriggeredAtUtc.Value &&
                                quote.AvailableAtUtc - open.ExitTriggeredAtUtc.Value <= MaximumQuoteAge &&
                                CanFill(quote, open.RemainingQuantity, buy: false))
                            {
                                closed.Add(Close(open, quote, parameters,
                                    open.RemainingQuantity, open.ExitReason));
                                Consume(quote, open.RemainingQuantity, buy: false);
                                openPositions.Remove(quote.Symbol);
                                pendingAddOns.Remove(quote.Symbol);
                            }
                        }
                        else if (!open.Unpriced &&
                                 quote.AvailableAtUtc > open.EntryAtUtc)
                        {
                            if (quote.AvailableAtUtc > open.DeadlineUtc)
                            {
                                // The time exit was triggered at its deadline, not by
                                // the later quote used to model the fill.
                                if (quote.AvailableAtUtc - open.DeadlineUtc <= MaximumQuoteAge &&
                                    CanFill(quote, open.RemainingQuantity, buy: false))
                                {
                                    open.ExitReason = "TimeExit";
                                    closed.Add(Close(open, quote, parameters,
                                        open.RemainingQuantity,
                                        open.ExitReason));
                                    Consume(quote, open.RemainingQuantity, buy: false);
                                    openPositions.Remove(quote.Symbol);
                                    pendingAddOns.Remove(quote.Symbol);
                                }
                                else if (quote.AvailableAtUtc - open.DeadlineUtc > MaximumQuoteAge)
                                    open.Unpriced = true;
                            }
                            else if ((canMark || open.Candidate.TriggerOnLastPrice) && decisionMark <= open.StopPrice)
                            {
                                open.ExitTriggeredAtUtc = quote.AvailableAtUtc;
                                open.ExitReason = "ObservedStop";
                            }
                            else if ((canMark || open.Candidate.TriggerOnLastPrice) && decisionMark >= open.TargetPrice)
                                quoteTargets.Add(quote.Symbol);
                        }
                    }
                    if (openPositions.ContainsKey(quote.Symbol))
                    {
                        if (!parameters.RequireDisplayedFillSize || quote.HasExecutableBook())
                            open.LatestPrice = SellReferencePrice(quote);
                        open.LastQuoteAtUtc = quote.AvailableAtUtc;
                    }
                }
                if (pendingEntries.TryGetValue(quote.Symbol, out var pendingEntry) &&
                    !openPositions.ContainsKey(quote.Symbol) &&
                    quote.AvailableAtUtc > pendingEntry.DecidedAtUtc &&
                    quote.AvailableAtUtc - pendingEntry.DecidedAtUtc <= MaximumQuoteAge)
                {
                    if (pendingEntry.DeferredNiftyAdmission is not null ||
                        pendingEntry.RequiresDeferredQuoteAdmission)
                    {
                        if (parameters.RequireDisplayedFillSize && !CanFill(quote, 1, buy: true))
                            continue;
                        quoteAdmissions.Add(quote.Symbol);
                        continue;
                    }
                    if (!CanFill(quote, pendingEntry.Quantity, buy: true)) continue;
                    var entryPrice = BuyReferencePrice(quote) *
                        (1m + parameters.EntrySlippagePercent);
                    if (pendingEntry.PlannedStopPrice is { } plannedStop &&
                        (plannedStop <= 0m || plannedStop >= entryPrice) ||
                        pendingEntry.PlannedTargetPrice is { } plannedTarget &&
                        (plannedTarget <= entryPrice || plannedTarget <= 0m))
                    {
                        // The as-of plan is no longer valid at the later fill.
                        // Do not silently reprice it using future information.
                        pendingEntries.Remove(quote.Symbol);
                        missingEntry++;
                        continue;
                    }
                    var plannedDeadline = pendingEntry.PlannedExitAtUtc ??
                        pendingEntry.DecidedAtUtc.AddMinutes(
                            parameters.MaximumHoldMinutes);
                    if (plannedDeadline <= quote.AvailableAtUtc)
                    {
                        pendingEntries.Remove(quote.Symbol);
                        missingEntry++;
                        continue;
                    }
                    openPositions.Add(quote.Symbol, new Position(pendingEntry,
                        quote.AvailableAtUtc, entryPrice,
                        pendingEntry.PlannedStopPrice ??
                            entryPrice * (1m - parameters.StopPercent),
                        pendingEntry.PlannedTargetPrice ??
                            entryPrice * (1m + parameters.TargetPercent),
                        plannedDeadline < sessionEndUtc ? plannedDeadline : sessionEndUtc,
                        SellReferencePrice(quote), ExchangeFor(quote.Symbol),
                        quote.OfferPrice is > 0m
                            ? "ObservedOffer" : quote.Source == "GrowwChainReceiptLtp"
                                ? "ObservedChainLtpNonExecutable" : "ModeledLastPrice"));
                    Consume(quote, pendingEntry.Quantity, buy: true);
                    peakEntryCapitalExposure = Math.Max(peakEntryCapitalExposure,
                        openPositions.Values.Sum(value => value.EntryPrice *
                            value.RemainingQuantity));
                    peakConcurrentPositions = Math.Max(peakConcurrentPositions,
                        openPositions.Count);
                    lastEntryAt = quote.AvailableAtUtc;
                    pendingEntries.Remove(quote.Symbol);
                }
            }
            if (quoteAdmissions.Count > 0)
            {
                var snapshot = Snapshot(time);
                foreach (var symbol in quoteAdmissions)
                {
                    var pending = pendingEntries[symbol];
                    if (pending.MaximumAdmissionQuoteDelaySeconds is { } maximumDelay &&
                        (time - pending.DecidedAtUtc > TimeSpan.FromSeconds(maximumDelay) ||
                        latest[symbol].BidPrice is not > 0m || latest[symbol].OfferPrice is not > 0m))
                    {
                        pendingEntries.Remove(symbol);
                        missingEntry++;
                        stale++;
                        FlagUncertainty(pending.StrategyDecisionAtUtc ?? pending.DecidedAtUtc);
                        continue;
                    }
                    var admitted = policy.AssessDeferredEntry(snapshot, pending,
                        latest[symbol]);
                    pendingEntries.Remove(symbol);
                    if (admitted is null) continue;
                    if (admitted.Symbol != symbol ||
                        admitted.DecidedAtUtc != time ||
                        admitted.DeferredNiftyAdmission is not null ||
                        admitted.RequiresDeferredQuoteAdmission ||
                        admitted.Quantity <= 0 ||
                        admitted.PlannedStopPrice is not > 0m ||
                        admitted.PlannedTargetPrice is not > 0m)
                        throw new InvalidOperationException(
                            "Deferred quote admission emitted an invalid entry plan.");
                    pendingEntries.Add(symbol, admitted);
                }
            }
            // A protective policy may react to a newly observed option quote
            // without waiting for another completed index bar. The quote is
            // visible only after its entire timestamp batch is processed; a
            // resulting stop can fill no earlier than a subsequent quote.
            if (quoteIndex != firstQuoteIndex && openPositions.Count > 0 &&
                time >= sessionStartUtc && time < sessionEndUtc)
            {
                var snapshot = Snapshot(time);
                ApplyStopUpdates(snapshot);
                snapshot = Snapshot(time);
                foreach (var symbol in quoteTargets)
                {
                    if (!openPositions.TryGetValue(symbol, out var open) ||
                        open.ExitTriggeredAtUtc is not null) continue;
                    var view = snapshot.OpenPositions.Single(value =>
                        value.Symbol == symbol);
                    if (policy.ShouldExtendTarget(snapshot, view)) continue;
                    open.ExitTriggeredAtUtc = time;
                    open.ExitReason = "ObservedTarget";
                    pendingAddOns.Remove(symbol);
                }
                QueuePolicyExits(snapshot);
                QueueAddOns(snapshot);
                QueuePartialExits(snapshot, filledAddOnsThisTime);
            }
            // Same-timestamp leg quotes have unknown internal ordering. Mark
            // the combined portfolio once after the entire timestamp batch;
            // otherwise an invented intermediate leg order distorts drawdown.
            var markedEquity = closed.Sum(value => value.NetPnl);
            foreach (var position in openPositions.Values)
            {
                if (position.Unpriced || time - position.LastQuoteAtUtc >
                    MaximumQuoteAge) continue;
                var liquidation = Math.Max(0.01m, position.LatestPrice *
                    (1m - parameters.ExitSlippagePercent));
                var estimatedCosts = EstimatedCosts(position, liquidation,
                    position.RemainingQuantity, position.ExchangeCode);
                markedEquity += (liquidation - position.EntryPrice) *
                    position.RemainingQuantity - estimatedCosts;
            }
            if (openPositions.Values.All(position => !position.Unpriced &&
                time - position.LastQuoteAtUtc <= MaximumQuoteAge &&
                latest.TryGetValue(position.Candidate.Symbol, out var mark) &&
                CanFill(mark, position.RemainingQuantity, buy: false)))
            {
                equityPeak = Math.Max(equityPeak, markedEquity);
                markToMarketDrawdown = Math.Max(markToMarketDrawdown,
                    equityPeak - markedEquity);
            }
        }
        missingEntry += pendingEntries.Count;
        missingAddOn += pendingAddOns.Count;
        missingExit += openPositions.Count;
        foreach (var position in openPositions.Values) FlagUncertainty(position.EntryAtUtc);
        foreach (var entry in pendingEntries.Values) FlagUncertainty(entry.StrategyDecisionAtUtc ?? entry.DecidedAtUtc);
        foreach (var addOn in pendingAddOns.Values) FlagUncertainty(addOn.DecidedAtUtc);
        var equity = closed.Sum(value => value.NetPnl);
        return new(closed, candidateCount, missingEntry, missingExit, stale,
            equity, markToMarketDrawdown,
            "Portfolio-aware completed-bar replay. Entry and stop/target exit fills require " +
            "a later fresh option quote; time exits use the first fresh post-deadline quote. " +
            (parameters.RequireDisplayedFillSize
                ? "Fills wait for fresh recorded top-of-book size; drawdown uses only size-supported liquidation marks. "
                : "Maximum drawdown includes sampled open-position liquidation marks. Observed offer/bid are used when present; otherwise option LTP is a non-executable proxy. ") +
            "Configured slippage and charges are modeled. Concurrent " +
            "positions require explicit research capacity. Partial reductions " +
            "fill only on later fresh quotes; entry costs are allocated once " +
            "across exit tranches. Same-contract winner add-ons use a later " +
            "fresh buy quote and separately charged entry; their pre-add-on " +
            "marks cannot establish a whole-size profit peak. Production portfolio " +
            "risk and specialty-strategy decisions are not reproduced.")
        {
            PeakEntryCapitalExposure = peakEntryCapitalExposure,
            PeakConcurrentPositions = peakConcurrentPositions,
            MissingAddOnQuoteCount = missingAddOn,
            UnsupportedPartialCount = unsupportedPartials.Count,
            FirstExecutionUncertaintyAtUtc = firstUncertainty
        };
    }

    private static HistoricalReplayFill Close(Position open, HistoricalReplayQuote exit,
        HistoricalReplayParameters parameters,
        int quantity, string reason)
    {
        var exitPrice = Math.Max(0.01m, SellReferencePrice(exit) *
            (1m - parameters.ExitSlippagePercent));
        var gross = (exitPrice - open.EntryPrice) * quantity;
        var costs = EstimatedCosts(open, exitPrice, quantity, open.ExchangeCode);
        decimal ModeledNetAt(decimal observedPrice)
        {
            var modeledExit = Math.Max(0.01m, observedPrice *
                (1m - parameters.ExitSlippagePercent));
            var modeledCosts = EstimatedCosts(open, modeledExit,
                quantity, open.ExchangeCode);
            return (modeledExit - open.EntryPrice) * quantity -
                modeledCosts;
        }
        // Earlier premium marks carried a smaller quantity before an add-on;
        // multiplying them by the final size would invent a basket peak.
        decimal? sampledPeak = open.AddOnQuantity > 0 ? null :
            Math.Max(0m, ModeledNetAt(open.HighestObservedPrice));
        decimal? sampledAdverse = open.AddOnQuantity > 0 ? null :
            Math.Min(0m, ModeledNetAt(open.LowestObservedPrice));
        var net = gross - costs;
        var partial = quantity < open.RemainingQuantity;
        open.EntryCostAllocated += EntryCostShare(open, quantity,
            open.ExchangeCode);
        return new(open.Candidate.Symbol, open.Candidate.Strategy,
            open.Candidate.StrategyDecisionAtUtc ?? open.Candidate.DecidedAtUtc,
            open.EntryAtUtc, exit.AvailableAtUtc,
            open.EntryPrice, exitPrice, quantity, gross,
            costs, net, reason)
        {
            IsPartialExit = partial,
            ExchangeCode = open.ExchangeCode,
            OriginalQuantity = open.TotalQuantity,
            EntryUnits = open.Candidate.Quantity, OriginalEntryPremium = open.BaseEntryPrice,
            AddOnAt = open.AddOnAt, AddOnUnits = open.AddOnQuantity, AddOnPremium = open.AddOnPrice,
            EntryPriceSource = open.EntryPriceSource,
            ExitPriceSource = exit.BidPrice is > 0m
                ? "ObservedBid" : exit.Source == "GrowwChainReceiptLtp"
                    ? "ObservedChainLtpNonExecutable" : "ModeledLastPrice",
            InitialStopPrice = open.InitialStopPrice,
            FinalStopPrice = open.StopPrice,
            InitialTargetPrice = open.TargetPrice,
            FinalTargetPrice = open.TargetPrice,
            ObservedPeakNetPnl = sampledPeak,
            ObservedAdverseNetPnl = sampledAdverse,
            SampledProfitGiveback = sampledPeak is { } peak
                ? Math.Max(0m, peak - net) : null
        };
    }

    private static decimal EstimatedCosts(Position open, decimal exitPrice,
        int quantity, string exchangeCode)
    {
        if (open.EntryCostAllocated == 0m &&
            quantity == open.TotalQuantity)
            return PaperTradingCostModel.CalculateOptionCharges([
                .. EntryTransactions(open),
                new(PaperOptionTransactionSide.Sell, exitPrice, quantity)
            ], exchangeCode).Total;
        return EntryCostShare(open, quantity, exchangeCode) +
            PaperTradingCostModel.CalculateOptionCharges([
                new(PaperOptionTransactionSide.Sell, exitPrice, quantity)
            ], exchangeCode).Total;
    }

    private static decimal EntryCostShare(Position open, int quantity,
        string exchangeCode)
    {
        var total = PaperTradingCostModel.CalculateOptionCharges(
            EntryTransactions(open), exchangeCode).Total;
        var unallocated = Math.Max(0m, total - open.EntryCostAllocated);
        return quantity == open.RemainingQuantity
            ? unallocated
            : Math.Min(unallocated,
                decimal.Round(total * quantity / open.TotalQuantity,
                    2, MidpointRounding.AwayFromZero));
    }

    private static PaperOptionTransaction[] EntryTransactions(Position open) =>
        open.AddOnPrice is { } addOnPrice
            ? [new(PaperOptionTransactionSide.Buy, open.BaseEntryPrice,
                   open.Candidate.Quantity),
               new(PaperOptionTransactionSide.Buy, addOnPrice,
                   open.AddOnQuantity)]
            : [new(PaperOptionTransactionSide.Buy, open.BaseEntryPrice,
                open.Candidate.Quantity)];

    private static decimal BuyReferencePrice(HistoricalReplayQuote quote) =>
        quote.OfferPrice is > 0m ? quote.OfferPrice.Value : quote.LastPrice;

    private static decimal SellReferencePrice(HistoricalReplayQuote quote) =>
        quote.BidPrice is > 0m ? quote.BidPrice.Value : quote.LastPrice;

    private static (HistoricalOptionChainFrame? Latest,
        HistoricalOptionChainFrame? Previous) VisibleChains(
        HistoricalOptionChainFrame[] chains, int observedCount,
        DateTimeOffset decisionAtUtc, string? underlying = null)
    {
        // Production selects the two newest *source* timestamps, not the
        // latest receipt. Receipt time still gates as-of visibility so a
        // delayed or out-of-order arrival cannot leak into an earlier frame.
        var eligible = chains.Take(observedCount)
            .Where(value => (underlying is null ||
                    value.UnderlyingSymbol == underlying) &&
                value.AvailableAtUtc < decisionAtUtc &&
                value.SourceTimestampUtc >=
                    decisionAtUtc.AddMinutes(-3))
            .OrderByDescending(value => value.SourceTimestampUtc)
            .ThenByDescending(value => value.AvailableAtUtc)
            .Take(2).ToArray();
        if (eligible.Length == 0) return (null, null);
        return (eligible[0], eligible.Length > 1 &&
            eligible[1].ExpiryDate == eligible[0].ExpiryDate
                ? eligible[1] : null);
    }

    private static void ValidateInputs(IReadOnlyList<HistoricalReplayBar> bars,
        IReadOnlyList<HistoricalReplayQuote> quotes,
        IReadOnlyList<HistoricalOptionChainFrame> chains, CancellationToken cancellationToken)
    {
        if (bars.Any(value => string.IsNullOrWhiteSpace(value.Symbol) ||
                value.IntervalSeconds <= 0 || value.Open <= 0m || value.Close <= 0m ||
                value.Low <= 0m || value.High < Math.Max(value.Open, value.Close) ||
                value.Low > Math.Min(value.Open, value.Close) || value.Volume < 0 ||
                value.OpenInterest is < 0m) ||
            bars.GroupBy(value => (value.Symbol, value.OpenTimeUtc,
                    value.IntervalSeconds)).Any(group => group.Count() > 1))
            throw new ArgumentException("Replay bars contain invalid or duplicate observations.");
        HistoricalReplayQuote? previous = null;
        foreach (var value in HistoricalQuotePaths.Ordered(quotes))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(value.Symbol) ||
                value.LastPrice <= 0m || value.Volume is < 0 ||
                value.OpenInterest is < 0m || value.BidPrice is <= 0m ||
                value.SourceAtUtc > value.AvailableAtUtc || value.DepthSourceAtUtc > value.AvailableAtUtc ||
                value.OfferPrice is <= 0m ||
                value.BidPrice is not null && value.OfferPrice is not null &&
                value.BidPrice > value.OfferPrice ||
                previous is not null && HistoricalQuoteOrderComparer.Instance.Compare(previous, value) >= 0)
                throw new ArgumentException("Replay quotes contain invalid or duplicate observations.");
            previous = value;
        }
        if (chains.Any(value => value.SourceTimestampUtc > value.AvailableAtUtc ||
                value.Strikes is null || value.Strikes.Any(strike =>
                    strike.Strike <= 0m || strike.CallOpenInterest < 0m ||
                    strike.PutOpenInterest < 0m)) ||
            chains.GroupBy(value => (value.UnderlyingSymbol,
                    value.ExpiryDate, value.AvailableAtUtc))
                .Any(group => group.Count() > 1) ||
            chains.Any(value => value.UnderlyingSymbol.Length == 0) &&
            chains.Any(value => value.UnderlyingSymbol.Length > 0))
            throw new ArgumentException("Replay option-chain snapshots are invalid or ambiguous.");
    }

    private sealed class Position(HistoricalReplayCandidate candidate,
        DateTimeOffset entryAtUtc, decimal entryPrice, decimal stopPrice,
        decimal targetPrice, DateTimeOffset deadlineUtc, decimal latestPrice,
        string exchangeCode, string entryPriceSource)
    {
        public HistoricalReplayCandidate Candidate { get; } = candidate;
        public int RemainingQuantity { get; set; } = candidate.Quantity;
        public int AddOnQuantity { get; set; }
        public decimal? AddOnPrice { get; set; }
        public DateTimeOffset? AddOnAt { get; set; }
        public int TotalQuantity => Candidate.Quantity + AddOnQuantity;
        public decimal EntryCostAllocated { get; set; }
        public DateTimeOffset EntryAtUtc { get; } = entryAtUtc;
        public decimal BaseEntryPrice { get; } = entryPrice;
        public decimal EntryPrice => AddOnPrice is { } addOnPrice
            ? (BaseEntryPrice * Candidate.Quantity +
                addOnPrice * AddOnQuantity) / TotalQuantity
            : BaseEntryPrice;
        public decimal InitialStopPrice { get; } = stopPrice;
        public decimal StopPrice { get; set; } = stopPrice;
        public decimal TargetPrice { get; } = targetPrice;
        public DateTimeOffset DeadlineUtc { get; } = deadlineUtc;
        public DateTimeOffset LastQuoteAtUtc { get; set; } = entryAtUtc;
        public DateTimeOffset? ExitTriggeredAtUtc { get; set; }
        public decimal LatestPrice { get; set; } = latestPrice;
        public string EntryPriceSource { get; } = entryPriceSource;
        public string ExchangeCode { get; } = exchangeCode;
        public decimal HighestObservedPrice { get; set; } = latestPrice;
        public decimal HighestDecisionPrice { get; set; } = latestPrice;
        public decimal LowestObservedPrice { get; set; } = latestPrice;
        public string ExitReason { get; set; } = "TimeExit";
        public bool Unpriced { get; set; }
    }
}
