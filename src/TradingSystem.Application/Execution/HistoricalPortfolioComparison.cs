using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TradingSystem.Application.Execution;

public sealed record HistoricalReplaySessionData(DateOnly Date, string IndexSymbol,
    IReadOnlyList<HistoricalReplayBar> Bars,
    IReadOnlyList<HistoricalReplayQuote> Quotes,
    DateTimeOffset StartUtc, DateTimeOffset EndUtc)
{
    // Canonical, non-secret policy inputs outside the bar/quote arrays (e.g.
    // lot sizes, expiry, prior close). Included in the data fingerprint.
    public string PolicyInputManifest { get; init; } = string.Empty;
    public bool RequiredInputsComplete { get; init; }
    // These must be independently attested by the archive builder. Complete
    // underlying candles do not prove the eligible option universe or the
    // strategy's quote/OI/spread inputs were captured as-of each decision.
    public bool EligibleOptionUniverseComplete { get; init; }
    public bool StrategyInputsComplete { get; init; }
    // Independent provenance is required; OHLC-derived last prices cannot
    // certify executable option entry/exit sides.
    public bool ExecutableQuoteSidesComplete { get; init; }
    // Exchange-specific costs are modeled, but a historical date needs its
    // own verified schedule or contract-note evidence before a credible verdict.
    public bool HistoricalChargesVerified { get; init; }
    public IReadOnlyList<HistoricalOptionChainFrame> OptionChains { get; init; } = [];
    public IReadOnlyList<HistoricalReplayControlEvent> ControlEvents { get; init; } = [];
    public IReadOnlyList<string> ExecutionOptionSymbols { get; init; } = [];
    public string ExchangeCode { get; init; } = "NSE";
    public IReadOnlyList<string> UnsupportedContractCoverage { get; init; } = [];
}

public sealed record HistoricalPortfolioComparisonDay(DateOnly Date,
    HistoricalReplayResult Baseline, HistoricalReplayResult Proposal,
    decimal NetDifference, HistoricalIndexMinuteCoverage IndexCoverage,
    bool CompleteQuotePaths)
{
    public string IndexSymbol { get; init; } = string.Empty;
    public string DescriptiveRegime { get; init; } = "Unknown";
}

public sealed record HistoricalRegimeComparison(string Regime, int Sessions,
    decimal BaselineNet, decimal ProposalNet, decimal NetDifference,
    int BaselineTrades, int ProposalTrades);

public sealed record HistoricalDateComparison(DateOnly Date, int Markets,
    decimal BaselineNet, decimal ProposalNet, decimal NetDifference,
    decimal BaselineCosts, decimal ProposalCosts,
    int BaselineTrades, int ProposalTrades);

public sealed record HistoricalPortfolioComparisonResult(string DataFingerprint,
    string RunFingerprint,
    string BaselineCodeVersion, string ProposalCodeVersion,
    IReadOnlyList<HistoricalPortfolioComparisonDay> Days,
    int TrainingDays, int ValidationDays, decimal TrainingNetDifference,
    decimal ValidationNetDifference, string EvidenceStatus)
{
    public IReadOnlyList<string> EvidenceGaps { get; init; } = [];
    public string PolicyConfigurationFingerprint { get; init; } = string.Empty;
    public IReadOnlyList<HistoricalDateComparison> DateComparisons => Days
        .GroupBy(value => value.Date).OrderBy(value => value.Key)
        .Select(group => new HistoricalDateComparison(group.Key, group.Count(),
            group.Sum(value => value.Baseline.NetPnl),
            group.Sum(value => value.Proposal.NetPnl),
            group.Sum(value => value.NetDifference),
            group.Sum(value => value.Baseline.TotalCosts),
            group.Sum(value => value.Proposal.TotalCosts),
            group.Sum(value => value.Baseline.CompletedPositions.Count),
            group.Sum(value => value.Proposal.CompletedPositions.Count))).ToArray();
    public IReadOnlyList<HistoricalRegimeComparison> RegimeComparisons => Days
        .GroupBy(value => value.DescriptiveRegime, StringComparer.Ordinal)
        .OrderBy(value => value.Key, StringComparer.Ordinal)
        .Select(group => new HistoricalRegimeComparison(group.Key,
            group.Count(), group.Sum(value => value.Baseline.NetPnl),
            group.Sum(value => value.Proposal.NetPnl),
            group.Sum(value => value.NetDifference),
            group.Sum(value => value.Baseline.CompletedPositions.Count),
            group.Sum(value => value.Proposal.CompletedPositions.Count))).ToArray();
    public decimal BaselineNet => Days.Sum(value => value.Baseline.NetPnl);
    public decimal ProposalNet => Days.Sum(value => value.Proposal.NetPnl);
    public int BaselineTradeCount => Days.Sum(value =>
        value.Baseline.CompletedPositions.Count);
    public int ProposalTradeCount => Days.Sum(value =>
        value.Proposal.CompletedPositions.Count);
    public decimal BaselineWinRate => BaselineTradeCount == 0 ? 0m :
        (decimal)Days.Sum(value => value.Baseline.Wins) / BaselineTradeCount;
    public decimal ProposalWinRate => ProposalTradeCount == 0 ? 0m :
        (decimal)Days.Sum(value => value.Proposal.Wins) / ProposalTradeCount;
    public decimal BaselineExpectancy => BaselineTradeCount == 0 ? 0m :
        BaselineNet / BaselineTradeCount;
    public decimal ProposalExpectancy => ProposalTradeCount == 0 ? 0m :
        ProposalNet / ProposalTradeCount;
    public decimal BaselineTotalCosts => Days.Sum(value => value.Baseline.TotalCosts);
    public decimal ProposalTotalCosts => Days.Sum(value => value.Proposal.TotalCosts);
    public decimal? BaselineSampledGiveback => Days.All(value =>
        value.Baseline.TotalSampledProfitGiveback.HasValue) ?
        Days.Sum(value => value.Baseline.TotalSampledProfitGiveback) : null;
    public decimal? ProposalSampledGiveback => Days.All(value =>
        value.Proposal.TotalSampledProfitGiveback.HasValue) ?
        Days.Sum(value => value.Proposal.TotalSampledProfitGiveback) : null;
    public decimal BaselineWorstSessionDrawdown => Days.Count == 0 ? 0m :
        Days.Max(value => value.Baseline.MaximumDrawdown);
    public decimal ProposalWorstSessionDrawdown => Days.Count == 0 ? 0m :
        Days.Max(value => value.Proposal.MaximumDrawdown);
    public decimal BaselineBetweenDayDrawdown => BetweenDayDrawdown(
        DateComparisons.Select(value => value.BaselineNet));
    public decimal ProposalBetweenDayDrawdown => BetweenDayDrawdown(
        DateComparisons.Select(value => value.ProposalNet));

    private static decimal BetweenDayDrawdown(IEnumerable<decimal> dailyNet)
    {
        var equity = 0m;
        var peak = 0m;
        var maximum = 0m;
        foreach (var day in dailyNet)
        {
            equity += day;
            peak = Math.Max(peak, equity);
            maximum = Math.Max(maximum, peak - equity);
        }
        return maximum;
    }
}

// Offline paired comparison: both versions receive the exact same frozen bars
// and quotes, but maintain independent simulated portfolio state. It cannot
// call a broker or mutate the production session.
public static class HistoricalPortfolioComparison
{
    public static HistoricalReplayEligibleComparison RunEligible(
        IReadOnlyList<HistoricalReplaySessionData> sessions,
        IReadOnlyList<string> requiredMarkets,
        Func<DateOnly, string, IHistoricalReplayPortfolioPolicy> baselineFactory,
        Func<DateOnly, string, IHistoricalReplayPortfolioPolicy> proposalFactory,
        HistoricalReplayParameters baselineParameters,
        HistoricalReplayParameters proposalParameters)
    {
        var selection = HistoricalReplayCohortSelector.Select(sessions,
            requiredMarkets);
        var comparison = selection.IncludedSessions.Count == 0 ? null :
            Run(selection.IncludedSessions, baselineFactory, proposalFactory,
                baselineParameters, proposalParameters);
        return new(selection, comparison);
    }

    public static HistoricalPortfolioComparisonResult Run(
        IReadOnlyList<HistoricalReplaySessionData> sessions,
        Func<DateOnly, IHistoricalReplayPortfolioPolicy> baselineFactory,
        Func<DateOnly, IHistoricalReplayPortfolioPolicy> proposalFactory,
        HistoricalReplayParameters baselineParameters,
        HistoricalReplayParameters proposalParameters)
    {
        ArgumentNullException.ThrowIfNull(baselineFactory);
        ArgumentNullException.ThrowIfNull(proposalFactory);
        return Run(sessions, (date, _) => baselineFactory(date),
            (date, _) => proposalFactory(date), baselineParameters,
            proposalParameters);
    }

    public static HistoricalPortfolioComparisonResult Run(
        IReadOnlyList<HistoricalReplaySessionData> sessions,
        Func<DateOnly, string, IHistoricalReplayPortfolioPolicy> baselineFactory,
        Func<DateOnly, string, IHistoricalReplayPortfolioPolicy> proposalFactory,
        HistoricalReplayParameters baselineParameters,
        HistoricalReplayParameters proposalParameters)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(baselineFactory);
        ArgumentNullException.ThrowIfNull(proposalFactory);
        baselineParameters.Validate();
        proposalParameters.Validate();
        var ordered = sessions.OrderBy(value => value.Date)
            .ThenBy(value => value.IndexSymbol, StringComparer.Ordinal).ToArray();
        if (ordered.Length == 0 || ordered.Select(value => (value.Date, value.IndexSymbol))
            .Distinct().Count() != ordered.Length)
            throw new ArgumentException("Replay market/date sessions must be nonempty and unique.",
                nameof(sessions));
        var days = new List<HistoricalPortfolioComparisonDay>(ordered.Length);
        var policyManifests = new List<(DateOnly Date, string IndexSymbol, string Baseline,
            string Proposal, bool BaselineParity, bool ProposalParity)>(ordered.Length);
        string? baselineVersion = null, proposalVersion = null;
        foreach (var session in ordered)
        {
            if (session.StartUtc >= session.EndUtc ||
                string.IsNullOrWhiteSpace(session.IndexSymbol) ||
                session.Bars is null || session.Quotes is null ||
                session.OptionChains is null)
                throw new ArgumentException("A replay session has invalid data or bounds.",
                    nameof(sessions));
            if (session.ExchangeCode is not ("NSE" or "BSE"))
                throw new ArgumentException("A supported exchange is required.",
                    nameof(sessions));
            var baseline = baselineFactory(session.Date, session.IndexSymbol);
            var proposal = proposalFactory(session.Date, session.IndexSymbol);
            if (baseline is null || proposal is null ||
                string.IsNullOrWhiteSpace(baseline.CodeVersion) ||
                string.IsNullOrWhiteSpace(proposal.CodeVersion))
                throw new ArgumentException("Both replay versions must be identified.");
            baselineVersion ??= baseline.CodeVersion;
            proposalVersion ??= proposal.CodeVersion;
            if (baselineVersion != baseline.CodeVersion ||
                proposalVersion != proposal.CodeVersion)
                throw new InvalidOperationException("Policy code versions changed within a comparison.");
            var baselineManifest = baseline.ConfigurationManifest;
            var proposalManifest = proposal.ConfigurationManifest;
            var baselineParity = baseline.ProductionPolicyParityVerified;
            var proposalParity = proposal.ProductionPolicyParityVerified;
            policyManifests.Add((session.Date, session.IndexSymbol,
                baselineManifest, proposalManifest,
                baselineParity, proposalParity));
            var coverage = HistoricalSessionCoverageEvaluator.Evaluate(session.Date,
                session.Bars.Where(value => value.Symbol == session.IndexSymbol &&
                        value.IntervalSeconds == 60)
                    .Select(value => value.OpenTimeUtc));
            var original = HistoricalPortfolioReplay.Run(session.Bars, session.Quotes,
                baseline, baselineParameters, session.StartUtc, session.EndUtc,
                session.OptionChains, session.ExchangeCode, sourceControls: session.ControlEvents);
            var changed = HistoricalPortfolioReplay.Run(session.Bars, session.Quotes,
                proposal, proposalParameters, session.StartUtc, session.EndUtc,
                session.OptionChains, session.ExchangeCode, sourceControls: session.ControlEvents);
            if (baselineManifest != baseline.ConfigurationManifest ||
                proposalManifest != proposal.ConfigurationManifest ||
                baselineParity != baseline.ProductionPolicyParityVerified ||
                proposalParity != proposal.ProductionPolicyParityVerified)
                throw new InvalidOperationException(
                    "A policy configuration or parity claim changed during its replay session.");
            var completePaths = original.MissingEntryQuoteCount == 0 &&
                original.MissingExitQuoteCount == 0 &&
                original.MissingAddOnQuoteCount == 0 &&
                original.UnsupportedPartialCount == 0 &&
                changed.MissingEntryQuoteCount == 0 &&
                changed.MissingExitQuoteCount == 0 &&
                changed.MissingAddOnQuoteCount == 0 &&
                changed.UnsupportedPartialCount == 0 &&
                original.StaleQuoteCount == 0 &&
                changed.StaleQuoteCount == 0;
            days.Add(new(session.Date, original, changed,
                changed.NetPnl - original.NetPnl, coverage, completePaths)
            {
                IndexSymbol = session.IndexSymbol,
                DescriptiveRegime = HistoricalSessionRegimeClassifier.Classify(
                    session, coverage)
            });
        }
        // Split whole trading dates, not individual markets: the three markets
        // on one date may share the same market shock and must stay together.
        var distinctDates = ordered.Select(value => value.Date).Distinct().ToArray();
        var trainingDateCount = distinctDates.Length < 2 ? distinctDates.Length :
            Math.Clamp((int)Math.Floor(distinctDates.Length * 0.70m), 1,
                distinctDates.Length - 1);
        var trainCount = ordered.Count(value => value.Date <=
            distinctDates[trainingDateCount - 1]);
        // Market sessions are replayed independently. Their totals can be
        // compared by date, but correlated exposure and shared capital cannot
        // be certified until a single cross-market portfolio clock exists.
        var independentCrossMarketSessions = distinctDates.Length != ordered.Length;
        var fingerprint = Fingerprint(ordered);
        var policyManifestJson = JsonSerializer.Serialize(policyManifests.Select(value => new
        { value.Date, value.IndexSymbol, value.Baseline, value.Proposal,
            value.BaselineParity, value.ProposalParity }));
        var policyConfigurationFingerprint = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(policyManifestJson)));
        var runFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            fingerprint + "|" + HistoricalPortfolioReplay.EngineVersion + "|" +
            baselineVersion + "|" + proposalVersion + "|" +
            policyManifestJson + "|" +
            JsonSerializer.Serialize(baselineParameters) + "|" +
            JsonSerializer.Serialize(proposalParameters) + "|" +
            string.Join('|', ordered.Select(value => value.ExchangeCode == "BSE" ?
                PaperTradingCostModel.BseScheduleVersion :
                PaperTradingCostModel.ScheduleVersion)))));
        var hasSufficientEvidence = distinctDates.Length >= 10 &&
            !independentCrossMarketSessions &&
            ordered.All(value => value.RequiredInputsComplete &&
                value.EligibleOptionUniverseComplete &&
                value.StrategyInputsComplete &&
                value.ExecutableQuoteSidesComplete &&
                value.HistoricalChargesVerified &&
                value.Quotes.Count > 0 && value.Quotes.Where(quote => quote.Symbol != value.IndexSymbol &&
                    !quote.Symbol.EndsWith("FUT", StringComparison.Ordinal)).All(quote => quote.HasRecordedBookEvidence()) &&
                !string.IsNullOrWhiteSpace(value.PolicyInputManifest)) &&
            policyManifests.All(value =>
                !string.IsNullOrWhiteSpace(value.Baseline) &&
                !string.IsNullOrWhiteSpace(value.Proposal) &&
                value.BaselineParity && value.ProposalParity) &&
            days.All(value => value.IndexCoverage.IsComplete && value.CompleteQuotePaths) &&
            days.Skip(trainCount).Sum(value =>
                value.Baseline.CompletedPositions.Count) >= 20 &&
            days.Skip(trainCount).Sum(value =>
                value.Proposal.CompletedPositions.Count) >= 20;
        var gaps = new List<string>();
        if (distinctDates.Length < 10) gaps.Add("Fewer than ten completed trading days.");
        if (independentCrossMarketSessions)
            gaps.Add("Same-day markets use independent replay state; correlated exposure and shared-capital risk are not modeled on one portfolio clock.");
        if (ordered.Any(value => !value.RequiredInputsComplete))
            gaps.Add("Underlying, front-future, prior-close, or dated instrument-master coverage is incomplete.");
        if (ordered.Any(value => !value.EligibleOptionUniverseComplete))
            gaps.Add("The full as-of eligible option universe has not been verified.");
        if (ordered.Any(value => !value.StrategyInputsComplete))
            gaps.Add("As-of strategy inputs such as option OI, spread and risk state are incomplete.");
        if (ordered.Any(value => !value.ExecutableQuoteSidesComplete ||
            value.Quotes.Count == 0 || value.Quotes.Where(quote => quote.Symbol != value.IndexSymbol &&
                !quote.Symbol.EndsWith("FUT", StringComparison.Ordinal)).Any(quote => !quote.HasRecordedBookEvidence())))
            gaps.Add("Observed, executable option bid/offer sides are incomplete; candle closes are only modeled fill proxies.");
        if (ordered.Any(value => !value.HistoricalChargesVerified))
            gaps.Add("Date-effective exchange, broker and statutory charges have not been verified for every session.");
        if (ordered.Any(value => string.IsNullOrWhiteSpace(value.PolicyInputManifest)))
            gaps.Add("A canonical policy-input manifest is missing.");
        if (policyManifests.Any(value =>
            string.IsNullOrWhiteSpace(value.Baseline) ||
            string.IsNullOrWhiteSpace(value.Proposal)))
            gaps.Add("A baseline or proposal policy-configuration manifest is missing.");
        if (policyManifests.Any(value => !value.BaselineParity ||
            !value.ProposalParity))
            gaps.Add("A baseline or proposal policy has not verified full production-policy parity; partial signal adapters cannot support a full-strategy verdict.");
        if (days.Any(value => !value.IndexCoverage.IsComplete))
            gaps.Add("One or more index sessions have missing minutes.");
        if (days.Any(value => !value.CompleteQuotePaths))
            gaps.Add("At least one simulated trade has missing/stale option quotes or an unexecutable partial lot.");
        if (days.Skip(trainCount).Sum(value =>
                value.Baseline.CompletedPositions.Count) < 20 ||
            days.Skip(trainCount).Sum(value =>
                value.Proposal.CompletedPositions.Count) < 20)
            gaps.Add("The held-out period has fewer than twenty trades per version.");
        return new(fingerprint, runFingerprint, baselineVersion!, proposalVersion!, days,
            trainingDateCount, distinctDates.Length - trainingDateCount,
            days.Take(trainCount).Sum(value => value.NetDifference),
            days.Skip(trainCount).Sum(value => value.NetDifference),
            hasSufficientEvidence ? "ResearchOnly" : "InsufficientCoverage")
        { EvidenceGaps = gaps,
            PolicyConfigurationFingerprint = policyConfigurationFingerprint };
    }

    internal static string Fingerprint(
        IEnumerable<HistoricalReplaySessionData> sessions, CancellationToken cancellationToken = default)
    {
        // Hash canonical rows incrementally: a full option universe must not
        // allocate another day-sized string plus its UTF-8 copy.
        using var hash = SHA256.Create();
        using var sink = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write);
        using var text = new StreamWriter(sink, new UTF8Encoding(false), 64 * 1024, leaveOpen: true);
        text.Write("historical-portfolio-input-v3\n");
        foreach (var session in sessions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            text.Write(FormattableString.Invariant($"{session.Date:yyyy-MM-dd}|{session.IndexSymbol}|{session.ExchangeCode}|{session.StartUtc.ToUniversalTime():O}|{session.EndUtc.ToUniversalTime():O}|{session.RequiredInputsComplete}|{session.EligibleOptionUniverseComplete}|{session.StrategyInputsComplete}|{session.ExecutableQuoteSidesComplete}|{session.HistoricalChargesVerified}|{session.PolicyInputManifest}\n"));
            text.Write("OPTIONS|");
            var first = true;
            foreach (var symbol in session.ExecutionOptionSymbols.OrderBy(value => value, StringComparer.Ordinal))
            {
                if (!first) text.Write(';');
                text.Write(symbol);
                first = false;
            }
            text.Write('\n');
            foreach (var control in HistoricalReplayControls.Validate(session.ControlEvents))
            {
                text.Write("E|");
                text.Write(JsonSerializer.Serialize(control));
                text.Write('\n');
            }
            foreach (var bar in session.Bars.OrderBy(value => value.AvailableAtUtc)
                         .ThenBy(value => value.Symbol, StringComparer.Ordinal)
                         .ThenBy(value => value.OpenTimeUtc)
                         .ThenBy(value => value.IntervalSeconds))
                text.Write(FormattableString.Invariant($"B|{bar.Symbol}|{bar.OpenTimeUtc.ToUniversalTime():O}|{bar.IntervalSeconds}|{bar.Open}|{bar.High}|{bar.Low}|{bar.Close}|{bar.Volume}|{bar.OpenInterest}|{bar.Source}\n"));
            foreach (var quote in HistoricalQuotePaths.Ordered(session.Quotes))
            {
                cancellationToken.ThrowIfCancellationRequested();
                text.Write(FormattableString.Invariant($"Q|{quote.Symbol}|{quote.AvailableAtUtc.ToUniversalTime():O}|{quote.LastPrice}|{quote.BidPrice}|{quote.OfferPrice}|{quote.BidQuantity}|{quote.OfferQuantity}|{quote.Volume}|{quote.OpenInterest}|{quote.Source}|{quote.SourceAtUtc:O}|{quote.DepthSourceAtUtc:O}\n"));
            }
            foreach (var chain in session.OptionChains.OrderBy(value =>
                         value.AvailableAtUtc).ThenBy(value => value.ExpiryDate))
            {
                text.Write(FormattableString.Invariant($"C|{chain.UnderlyingSymbol}|{chain.AvailableAtUtc.ToUniversalTime():O}|{chain.SourceTimestampUtc.ToUniversalTime():O}|{chain.ExpiryDate:yyyy-MM-dd}\n"));
                foreach (var strike in chain.Strikes.OrderBy(value => value.Strike))
                    text.Write(FormattableString.Invariant($"S|{strike.Strike}|{strike.CallOpenInterest}|{strike.PutOpenInterest}\n"));
            }
        }
        text.Flush();
        sink.FlushFinalBlock();
        return Convert.ToHexString(hash.Hash!);
    }
}
