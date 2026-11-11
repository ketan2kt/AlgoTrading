using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Execution;

// This is an offline research model. It deliberately has no broker, order, or live-state dependency.
public sealed record HistoricalReplayQuote(string Symbol, DateTimeOffset AvailableAtUtc,
    decimal LastPrice)
{
    // A chain receipt is an observed LTP, not an executable bid/offer. A
    // historical candle close is only a modeled price proxy.
    public string Source { get; init; } = "CandleCloseProxy";
    // Optional observed sides. Historical OHLC imports leave these null;
    // they must never manufacture a bid/offer from a candle close.
    public decimal? BidPrice { get; init; }
    public decimal? OfferPrice { get; init; }
    public long? BidQuantity { get; init; }
    public long? OfferQuantity { get; init; }
    public long? Volume { get; init; }
    public decimal? OpenInterest { get; init; }
    public DateTimeOffset? SourceAtUtc { get; init; }
    public DateTimeOffset? DepthSourceAtUtc { get; init; }

    public bool HasExecutableBook() => BidPrice is > 0m && OfferPrice is > 0m && BidPrice <= OfferPrice;
    // A received empty book explicitly records unavailable liquidity. It is
    // different from a REST LTP response which never supplied depth evidence.
    public bool HasRecordedBookEvidence() => HasExecutableBook() ||
        Source == "GrowwFeedSample" && DepthSourceAtUtc.HasValue &&
        DepthSourceAtUtc <= AvailableAtUtc && BidPrice is null or > 0m && OfferPrice is null or > 0m && !(BidPrice > OfferPrice);

    public bool IsFreshAsOf(DateTimeOffset asOf, TimeSpan maximumAge) =>
        AvailableAtUtc <= asOf && asOf - AvailableAtUtc <= maximumAge &&
        // Match the production receipt-age gate. A current standing bid/offer
        // does not become invalid solely because its last trade happened earlier.
        (SourceAtUtc is null || SourceAtUtc <= AvailableAtUtc) &&
        (DepthSourceAtUtc is null || DepthSourceAtUtc <= AvailableAtUtc);
}

public sealed record HistoricalReplayBar(DateTimeOffset OpenTimeUtc, int IntervalSeconds,
    decimal Open, decimal High, decimal Low, decimal Close)
{
    public DateTimeOffset AvailableAtUtc => OpenTimeUtc.AddSeconds(IntervalSeconds);
    public string Symbol { get; init; } = string.Empty;
    public long Volume { get; init; }
    public decimal? OpenInterest { get; init; }
    public string Source { get; init; } = "RecordedCandle";
}

public sealed record HistoricalReplayFrame(DateTimeOffset AvailableAtUtc,
    IReadOnlyList<HistoricalReplayBar> CompletedBars,
    IReadOnlyDictionary<string, HistoricalReplayQuote> LatestQuotes);

// Strategy versions implement this pure contract. Live entry policies can be
// extracted behind it without giving the historical runner a broker or database.
public interface IHistoricalReplayEntryPolicy
{
    string CodeVersion { get; }
    IReadOnlyList<HistoricalReplayCandidate> Evaluate(HistoricalReplayFrame frame);
}

public sealed record HistoricalReplayCandidate(string Symbol, DateTimeOffset DecidedAtUtc,
    string Strategy, decimal Confidence, int Quantity)
{
    // Required for modeled partial exits: exchange option quantities must be
    // whole lots. Existing full-position research candidates may omit it.
    public int? LotSize { get; init; }
    // Frozen at the as-of decision; a later modeled fill must not recalculate
    // protective levels from a quote the strategy could not yet have seen.
    public decimal? PlannedStopPrice { get; init; }
    public decimal? PlannedTargetPrice { get; init; }
    public Direction? UnderlyingDirection { get; init; }
    public MarketRegime? SignalRegime { get; init; }
    public DateOnly? ContractExpiryDate { get; init; }
    public DateTimeOffset? PlannedExitAtUtc { get; init; }
    // Production requests the option quote only after the index signal and
    // pre-quote gates. This freezes all pre-quote inputs until that later
    // observation arrives; the fill itself requires a further quote.
    public NiftyOptionAdmissionInput? DeferredNiftyAdmission { get; init; }
    public ProfessionalTradeAssessmentInput? DeferredSensexProfessionalContext { get; init; }
    // Other markets may also require an as-of option quote after signal discovery.
    public bool RequiresDeferredQuoteAdmission { get; init; }
    public int? MaximumAdmissionQuoteDelaySeconds { get; init; }
    public DateTimeOffset? StrategyDecisionAtUtc { get; init; }
    // Multi-market paper positions trigger on quote LTP, while Nifty's paper
    // position manager triggers on the observed bid. The modeled exit still
    // sells at a later bid when that side was captured.
    public bool TriggerOnLastPrice { get; init; }
    // Nifty production recalculates protection from initial risk before it
    // tests the new quote. Reversal-only protection is not a persisted floor.
    public bool RecomputesProtectionFromInitialRisk { get; init; }
}

public sealed record HistoricalReplayParameters(decimal StopPercent, decimal TargetPercent,
    decimal EntrySlippagePercent, decimal ExitSlippagePercent, int MaximumHoldMinutes,
    int MinimumMinutesBetweenEntries)
{
    // Explicit research-only capacity. The existing single-position replay
    // remains the default until a full portfolio risk policy is shared.
    public int MaximumConcurrentPositions { get; init; } = 1;
    // Explicit research assumption; never turn modeled fees into a claim that
    // the original broker contract-note charges were independently verified.
    public bool AllowModeledHistoricalCosts { get; init; }
    public static HistoricalReplayParameters Default { get; } =
        new(0.10m, 0.15m, 0.0025m, 0.0025m, 45, 5);

    public void Validate()
    {
        if (StopPercent is <= 0m or > 0.75m || TargetPercent is <= 0m or > 2m ||
            EntrySlippagePercent is < 0m or > 0.05m || ExitSlippagePercent is < 0m or > 0.05m ||
            MaximumHoldMinutes is < 1 or > 375 || MinimumMinutesBetweenEntries is < 0 or > 120 ||
            MaximumConcurrentPositions is < 1 or > 10)
            throw new ArgumentOutOfRangeException(nameof(HistoricalReplayParameters));
    }
}

public sealed record HistoricalReplayFill(string Symbol, string Strategy, DateTimeOffset DecisionAtUtc,
    DateTimeOffset EntryAtUtc, DateTimeOffset ExitAtUtc, decimal EntryPrice, decimal ExitPrice,
    int Quantity, decimal GrossPnl, decimal Costs, decimal NetPnl, string ExitReason)
{
    public string ExchangeCode { get; init; } = "NSE";
    public bool IsPartialExit { get; init; }
    public int OriginalQuantity { get; init; }
    public int EntryUnits { get; init; }
    public decimal? OriginalEntryPremium { get; init; }
    public DateTimeOffset? AddOnAt { get; init; }
    public int AddOnUnits { get; init; }
    public decimal? AddOnPremium { get; init; }
    public decimal? InitialStopPrice { get; init; }
    public decimal? FinalStopPrice { get; init; }
    public decimal? InitialTargetPrice { get; init; }
    public decimal? FinalTargetPrice { get; init; }
    public string EntryPriceSource { get; init; } = "ModeledLastPrice";
    public string ExitPriceSource { get; init; } = "ModeledLastPrice";
    // Sampled, modeled liquidation values. They are not executable intrabar peaks.
    public decimal? ObservedPeakNetPnl { get; init; }
    public decimal? ObservedAdverseNetPnl { get; init; }
    public decimal? SampledProfitGiveback { get; init; }
}

public sealed record HistoricalReplayResult(IReadOnlyList<HistoricalReplayFill> Trades,
    int CandidateCount, int MissingEntryQuoteCount, int MissingExitQuoteCount,
    int StaleQuoteCount, decimal NetPnl, decimal MaximumDrawdown,
    string ExecutionAssumption)
{
    public decimal PeakEntryCapitalExposure { get; init; }
    public int PeakConcurrentPositions { get; init; }
    public int MissingAddOnQuoteCount { get; init; }
    public int UnsupportedPartialCount { get; init; }
    public IReadOnlyList<HistoricalReplayFill> CompletedPositions =>
        HistoricalReplayTradeAggregation.CompletedPositions(Trades);
    public int Wins => CompletedPositions.Count(value => value.NetPnl > 0m);
    public decimal WinRate => CompletedPositions.Count == 0 ? 0m :
        (decimal)Wins / CompletedPositions.Count;
    public decimal Expectancy => CompletedPositions.Count == 0 ? 0m :
        CompletedPositions.Sum(value => value.NetPnl) / CompletedPositions.Count;
    public decimal TotalCosts => Trades.Sum(value => value.Costs);
    public decimal? TotalSampledProfitGiveback =>
        Trades.Any(value => value.IsPartialExit) && MissingExitQuoteCount > 0
            ? null
            : CompletedPositions.All(value => value.SampledProfitGiveback.HasValue)
                ? CompletedPositions.Sum(value => value.SampledProfitGiveback)
                : null;
    public decimal ProfitFactor
    {
        get
        {
            var losses = -CompletedPositions.Where(value => value.NetPnl < 0m)
                .Sum(value => value.NetPnl);
            return losses == 0m ? 0m : CompletedPositions.Where(value => value.NetPnl > 0m)
                .Sum(value => value.NetPnl) / losses;
        }
    }
}

public static class HistoricalSessionReplay
{
    private static readonly TimeSpan MaximumQuoteAge = TimeSpan.FromSeconds(90);

    public static HistoricalReplayResult RunWithPolicy(
        IReadOnlyList<HistoricalReplayBar> sourceBars,
        IReadOnlyList<HistoricalReplayQuote> sourceQuotes,
        IHistoricalReplayEntryPolicy policy,
        HistoricalReplayParameters parameters, DateTimeOffset sessionStartUtc,
        DateTimeOffset sessionEndUtc, string exchangeCode = "NSE")
    {
        ArgumentNullException.ThrowIfNull(sourceBars);
        ArgumentNullException.ThrowIfNull(sourceQuotes);
        ArgumentNullException.ThrowIfNull(policy);
        if (string.IsNullOrWhiteSpace(policy.CodeVersion))
            throw new ArgumentException("Replay strategy code version is required.", nameof(policy));
        if (sessionStartUtc >= sessionEndUtc)
            throw new ArgumentException("Replay session start must precede its end.");
        var bars = sourceBars.Where(value => value.IntervalSeconds > 0 &&
                value.AvailableAtUtc <= sessionEndUtc)
            .OrderBy(value => value.AvailableAtUtc)
            .ThenBy(value => value.Symbol, StringComparer.Ordinal)
            .ThenBy(value => value.OpenTimeUtc)
            .ThenBy(value => value.IntervalSeconds).ToArray();
        var quotes = sourceQuotes.Where(value => value.AvailableAtUtc <= sessionEndUtc)
            .OrderBy(value => value.AvailableAtUtc).ToArray();
        var visibleBars = new List<HistoricalReplayBar>();
        var latestQuotes = new Dictionary<string, HistoricalReplayQuote>(StringComparer.Ordinal);
        var decisions = new List<HistoricalReplayCandidate>();
        var quoteIndex = 0;
        foreach (var completedAt in bars.GroupBy(value => value.AvailableAtUtc))
        {
            visibleBars.AddRange(completedAt);
            var asOf = completedAt.Key;
            // A historical option quote and completed decision bar stamped at
            // the same instant have unknown ordering. Keep it invisible until
            // the next decision frame rather than granting a hindsight edge.
            while (quoteIndex < quotes.Length &&
                   quotes[quoteIndex].AvailableAtUtc < asOf)
            {
                var quote = quotes[quoteIndex++];
                latestQuotes[quote.Symbol] = quote;
            }
            // Prior-session bars are visible as warm-up context, but a strategy
            // cannot create a trade before this replayed session begins.
            if (asOf < sessionStartUtc) continue;
            var frame = new HistoricalReplayFrame(asOf,
                visibleBars.ToArray(),
                latestQuotes.Where(value => asOf - value.Value.AvailableAtUtc <=
                        MaximumQuoteAge)
                    .ToDictionary(value => value.Key, value => value.Value,
                        StringComparer.Ordinal));
            var proposed = policy.Evaluate(frame);
            if (proposed.Any(value => value.DecidedAtUtc != frame.AvailableAtUtc))
                throw new InvalidOperationException("A replay policy emitted a decision outside its as-of frame.");
            decisions.AddRange(proposed);
        }
        return Run(quotes, decisions, parameters, sessionEndUtc, exchangeCode);
    }

    public static HistoricalReplayResult Run(IReadOnlyList<HistoricalReplayQuote> sourceQuotes,
        IReadOnlyList<HistoricalReplayCandidate> sourceCandidates,
        HistoricalReplayParameters parameters, DateTimeOffset sessionEndUtc,
        string exchangeCode = "NSE")
    {
        parameters.Validate();
        if (exchangeCode is not ("NSE" or "BSE"))
            throw new ArgumentOutOfRangeException(nameof(exchangeCode));
        var quotes = sourceQuotes.Where(value => value.LastPrice > 0m &&
                value.AvailableAtUtc <= sessionEndUtc)
            .OrderBy(value => value.AvailableAtUtc).ThenBy(value => value.Symbol,
                StringComparer.Ordinal).ToArray();
        if (quotes.Any(value => string.IsNullOrWhiteSpace(value.Symbol) ||
                value.BidPrice is <= 0m || value.OfferPrice is <= 0m ||
                value.BidPrice is not null && value.OfferPrice is not null &&
                value.BidPrice > value.OfferPrice) ||
            quotes.GroupBy(value => (value.Symbol, value.AvailableAtUtc))
                .Any(group => group.Count() > 1))
            throw new ArgumentException("Replay quotes contain invalid or ambiguous observations.",
                nameof(sourceQuotes));
        var groupedQuotes = quotes.GroupBy(value => value.Symbol, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var candidates = sourceCandidates.Where(value => value.DecidedAtUtc < sessionEndUtc)
            .OrderBy(value => value.DecidedAtUtc).ThenBy(value => value.Symbol,
                StringComparer.Ordinal).ToArray();
        var fills = new List<HistoricalReplayFill>();
        var missingEntry = 0;
        var missingExit = 0;
        var stale = 0;
        DateTimeOffset? lastEntry = null;
        DateTimeOffset? occupiedUntil = null;
        foreach (var candidate in candidates)
        {
            if (candidate.Quantity <= 0 || string.IsNullOrWhiteSpace(candidate.Symbol))
                continue;
            // Only a quote received AFTER the decision can fill a new order. No same-sample fills.
            if (occupiedUntil > candidate.DecidedAtUtc ||
                (lastEntry.HasValue && candidate.DecidedAtUtc - lastEntry.Value <
                    TimeSpan.FromMinutes(parameters.MinimumMinutesBetweenEntries))) continue;
            if (!groupedQuotes.TryGetValue(candidate.Symbol, out var path))
            {
                missingEntry++;
                continue;
            }
            var entryIndex = Array.FindIndex(path, value => value.AvailableAtUtc > candidate.DecidedAtUtc);
            if (entryIndex < 0)
            {
                missingEntry++;
                continue;
            }
            var entry = path[entryIndex];
            if (entry.AvailableAtUtc - candidate.DecidedAtUtc > MaximumQuoteAge)
            {
                stale++;
                continue;
            }
            var deadline = candidate.DecidedAtUtc.AddMinutes(parameters.MaximumHoldMinutes);
            if (deadline > sessionEndUtc) deadline = sessionEndUtc;
            var entryPrice = (entry.OfferPrice ?? entry.LastPrice) *
                (1m + parameters.EntrySlippagePercent);
            var stop = entryPrice * (1m - parameters.StopPercent);
            var target = entryPrice * (1m + parameters.TargetPercent);
            HistoricalReplayQuote? exit = null;
            var exitReason = "TimeExit";
            for (var i = entryIndex + 1; i < path.Length && path[i].AvailableAtUtc <= deadline; i++)
            {
                var current = path[i];
                if (current.AvailableAtUtc - path[i - 1].AvailableAtUtc > MaximumQuoteAge)
                {
                    stale++;
                    exit = null;
                    break;
                }
                var sellMark = current.BidPrice ?? current.LastPrice;
                if (sellMark <= stop || sellMark >= target)
                {
                    exitReason = sellMark <= stop ? "ObservedStop" : "ObservedTarget";
                    // The observed touch triggers an exit; it cannot also be its fill.
                    // Use the next fresh observation, including any gap beyond the trigger.
                    exit = i + 1 < path.Length &&
                        path[i + 1].AvailableAtUtc - current.AvailableAtUtc <= MaximumQuoteAge &&
                        path[i + 1].AvailableAtUtc <= sessionEndUtc
                        ? path[i + 1] : null;
                    if (exit is null) stale++;
                    break;
                }
                exit = current;
            }
            if (exitReason == "TimeExit" && exit is not null)
            {
                // A time exit cannot fill at a price observed before the deadline.
                var terminal = path.FirstOrDefault(value => value.AvailableAtUtc > deadline &&
                    value.AvailableAtUtc <= deadline + MaximumQuoteAge);
                exit = terminal is not null && terminal.AvailableAtUtc - exit.AvailableAtUtc <=
                    MaximumQuoteAge ? terminal : null;
            }
            // Do not invent an exit when the option path ends or has a gap. Assume the
            // position remains occupied for the session, so later entries are not fabricated.
            if (exit is null)
            {
                missingExit++;
                occupiedUntil = sessionEndUtc;
                continue;
            }
            var exitPrice = Math.Max(0.01m, (exit.BidPrice ?? exit.LastPrice) *
                (1m - parameters.ExitSlippagePercent));
            var gross = (exitPrice - entryPrice) * candidate.Quantity;
            var costs = PaperTradingCostModel.CalculateOptionCharges([
                new(PaperOptionTransactionSide.Buy, entryPrice, candidate.Quantity),
                new(PaperOptionTransactionSide.Sell, exitPrice, candidate.Quantity)
            ], exchangeCode).Total;
            fills.Add(new(candidate.Symbol, candidate.Strategy, candidate.DecidedAtUtc,
                entry.AvailableAtUtc, exit.AvailableAtUtc, entryPrice, exitPrice,
                candidate.Quantity, gross, costs, gross - costs, exitReason)
            {
                ExchangeCode = exchangeCode,
                EntryPriceSource = entry.OfferPrice is > 0m
                    ? "ObservedOffer" : entry.Source == "GrowwChainReceiptLtp"
                        ? "ObservedChainLtpNonExecutable" : "ModeledLastPrice",
                ExitPriceSource = exit.BidPrice is > 0m
                    ? "ObservedBid" : exit.Source == "GrowwChainReceiptLtp"
                        ? "ObservedChainLtpNonExecutable" : "ModeledLastPrice",
                InitialStopPrice = stop,
                FinalStopPrice = stop,
                InitialTargetPrice = target,
                FinalTargetPrice = target
            });
            occupiedUntil = exit.AvailableAtUtc;
            lastEntry = entry.AvailableAtUtc;
        }
        decimal equity = 0m, peak = 0m, drawdown = 0m;
        foreach (var fill in fills.OrderBy(value => value.ExitAtUtc))
        {
            equity += fill.NetPnl;
            peak = Math.Max(peak, equity);
            drawdown = Math.Max(drawdown, peak - equity);
        }
        return new(fills, candidates.Length, missingEntry, missingExit, stale,
            equity, drawdown,
            "Observed offer/bid are used when present; otherwise option LTP or a candle " +
            "close is a non-executable proxy. Entry and triggered exit fills use the " +
            "next fresh quote plus configured slippage and the paper charge model; " +
            "stop/target touches between samples are unknown.");
    }
}
