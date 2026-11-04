using System.Text.Json.Serialization;

namespace TradingSystem.Application.Execution;

public sealed record HistoricalReplayExcludedDate(DateOnly Date,
    IReadOnlyList<string> Reasons);

public sealed record HistoricalReplayCohortSelection
{
    [JsonIgnore] public IReadOnlyList<HistoricalReplaySessionData> IncludedSessions { get; }
    public IReadOnlyList<HistoricalReplayExcludedDate> ExcludedDates { get; }
    public IReadOnlyList<DateOnly> IncludedDates { get; }
    public int IncludedSessionCount { get; }

    public HistoricalReplayCohortSelection(IReadOnlyList<HistoricalReplaySessionData> includedSessions,
        IReadOnlyList<HistoricalReplayExcludedDate> excludedDates)
    {
        IncludedSessions = includedSessions;
        ExcludedDates = excludedDates;
        IncludedDates = includedSessions.Select(value => value.Date).Distinct().OrderBy(value => value).ToArray();
        IncludedSessionCount = includedSessions.Count;
    }

    [JsonConstructor]
    public HistoricalReplayCohortSelection(IReadOnlyList<DateOnly> includedDates,
        IReadOnlyList<HistoricalReplayExcludedDate> excludedDates, int includedSessionCount)
    {
        IncludedSessions = [];
        IncludedDates = includedDates;
        ExcludedDates = excludedDates;
        IncludedSessionCount = includedSessionCount;
    }
}

public sealed record HistoricalReplayEligibleComparison(
    HistoricalReplayCohortSelection Cohort,
    HistoricalPortfolioComparisonResult? Comparison);

// Select on frozen input coverage only, before either strategy is evaluated.
// Filtering by simulated P&L, trade count or missing *strategy-specific* fills
// would create survivor bias and is intentionally left to the verdict gate.
public static class HistoricalReplayCohortSelector
{
    public static HistoricalReplayCohortSelection Select(
        IReadOnlyList<HistoricalReplaySessionData> sessions,
        IReadOnlyList<string> requiredMarkets,
        IReadOnlyList<DateOnly>? requestedDates = null,
        bool allowModeledHistoricalCosts = false)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(requiredMarkets);
        if (sessions.Any(value => value is null ||
            string.IsNullOrWhiteSpace(value.IndexSymbol)))
            throw new ArgumentException("Replay sessions require a market identity.",
                nameof(sessions));
        if (requestedDates is not null &&
            (requestedDates.Distinct().Count() != requestedDates.Count ||
             sessions.Any(value => !requestedDates.Contains(value.Date))))
            throw new ArgumentException("Requested replay dates must be unique and contain every session.",
                nameof(requestedDates));
        var markets = requiredMarkets.Select(value => value?.Trim().ToUpperInvariant())
            .ToArray();
        if (markets.Length == 0 || markets.Any(string.IsNullOrWhiteSpace) ||
            markets.Distinct(StringComparer.Ordinal).Count() != markets.Length ||
            sessions.GroupBy(value => (value.Date,
                    Market: value.IndexSymbol.Trim().ToUpperInvariant()))
                .Any(group => group.Count() > 1))
            throw new ArgumentException("Replay cohort markets and sessions must be unique.");

        var included = new List<HistoricalReplaySessionData>();
        var excluded = new List<HistoricalReplayExcludedDate>();
        var byDate = sessions.GroupBy(value => value.Date)
            .ToDictionary(value => value.Key, value => value.ToArray());
        foreach (var date in (requestedDates ?? byDate.Keys.ToArray()).OrderBy(value => value))
        {
            var byMarket = byDate.GetValueOrDefault(date, [])
                .ToDictionary(value =>
                value.IndexSymbol.Trim().ToUpperInvariant(),
                value => value, StringComparer.Ordinal);
            var reasons = new List<string>();
            foreach (var market in markets)
            {
                if (!byMarket.TryGetValue(market!, out var session))
                {
                    reasons.Add($"{market}: session archive missing");
                    continue;
                }
                if (session.Bars is null || session.Quotes is null ||
                    session.StartUtc >= session.EndUtc)
                {
                    reasons.Add($"{market}: invalid session bounds or observations");
                    continue;
                }
                var coverage = HistoricalSessionCoverageEvaluator.Evaluate(
                    session.Date, session.Bars.Where(value =>
                            value.Symbol == session.IndexSymbol &&
                            value.IntervalSeconds == 60)
                        .Select(value => value.OpenTimeUtc));
                if (!coverage.IsComplete || !session.RequiredInputsComplete)
                    reasons.Add($"{market}: required underlying, future, dated-master or control-state coverage incomplete");
                if (session.ControlEvents.Any(value => value.Kind == HistoricalReplayControlKind.ReconciliationHold && value.Enabled))
                    reasons.Add($"{market}: reconciliation outage prevents faithful execution replay");
                if (!session.EligibleOptionUniverseComplete ||
                    !session.StrategyInputsComplete)
                    reasons.Add($"{market}: option universe or strategy inputs incomplete");
                // Underlying spot freshness uses LTP; it does not need option
                // bid/offer sides. Do not reject every otherwise complete day
                // merely because the archived index quote has no order book.
                var optionSymbols = session.ExecutionOptionSymbols.ToHashSet(StringComparer.Ordinal);
                var optionQuotes = session.Quotes.Where(value => optionSymbols.Count > 0 ? optionSymbols.Contains(value.Symbol) :
                    value.Symbol != session.IndexSymbol && !value.Symbol.EndsWith("FUT", StringComparison.Ordinal));
                if (!session.ExecutableQuoteSidesComplete ||
                    !optionQuotes.Any() || optionQuotes.Any(value =>
                        value.BidPrice is not > 0m ||
                        value.OfferPrice is not > 0m || value.BidPrice > value.OfferPrice))
                    reasons.Add($"{market}: executable option bid/offer coverage incomplete");
                if (!session.HistoricalChargesVerified && !allowModeledHistoricalCosts)
                    reasons.Add($"{market}: date-effective charges unverified");
                if (string.IsNullOrWhiteSpace(session.PolicyInputManifest))
                    reasons.Add($"{market}: frozen policy-input manifest missing");
            }
            var selected = markets.Where(value => byMarket.ContainsKey(value!))
                .Select(value => byMarket[value!]).ToArray();
            if (selected.Select(value => (value.StartUtc, value.EndUtc))
                    .Distinct().Count() > 1)
                reasons.Add("Market session clocks are not aligned");
            if (reasons.Count > 0)
                excluded.Add(new(date, reasons));
            else
                included.AddRange(selected);
        }
        return new(included, excluded);
    }
}
