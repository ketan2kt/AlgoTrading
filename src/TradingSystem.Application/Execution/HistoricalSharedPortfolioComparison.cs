using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TradingSystem.Application.Execution;

public sealed record HistoricalSharedPortfolioDay(DateOnly Date,
    HistoricalReplayResult Baseline, HistoricalReplayResult Proposal)
{
    public decimal NetDifference => Proposal.NetPnl - Baseline.NetPnl;
    public string DescriptiveRegime { get; init; } = "Unknown";
}

public sealed record HistoricalSharedPortfolioRegimeComparison(string Regime,
    int Sessions, decimal BaselineNet, decimal ProposalNet,
    int BaselineTrades, int ProposalTrades);

public sealed record HistoricalSharedPortfolioPolicyMetrics(decimal Net,
    decimal Costs, int Trades, decimal WinRate, decimal Expectancy,
    decimal? SampledGiveback, decimal WorstSessionDrawdown,
    decimal BetweenDayDrawdown, decimal PeakEntryCapitalExposure,
    int PeakConcurrentPositions);

public sealed record HistoricalSharedPortfolioResult(
    HistoricalReplayCohortSelection Cohort,
    IReadOnlyList<HistoricalSharedPortfolioDay> Days,
    string DataFingerprint, string RunFingerprint,
    int TrainingDays, int ValidationDays,
    string EvidenceStatus, IReadOnlyList<string> EvidenceGaps)
{
    public decimal BaselineNet => Days.Sum(value => value.Baseline.NetPnl);
    public decimal ProposalNet => Days.Sum(value => value.Proposal.NetPnl);
    public int BaselineTrades => Days.Sum(value =>
        value.Baseline.CompletedPositions.Count);
    public int ProposalTrades => Days.Sum(value =>
        value.Proposal.CompletedPositions.Count);
    public decimal BaselineCosts => Days.Sum(value => value.Baseline.TotalCosts);
    public decimal ProposalCosts => Days.Sum(value => value.Proposal.TotalCosts);
    public decimal BaselineWinRate => BaselineTrades == 0 ? 0m :
        (decimal)Days.Sum(value => value.Baseline.Wins) / BaselineTrades;
    public decimal ProposalWinRate => ProposalTrades == 0 ? 0m :
        (decimal)Days.Sum(value => value.Proposal.Wins) / ProposalTrades;
    public decimal BaselineExpectancy => BaselineTrades == 0 ? 0m :
        BaselineNet / BaselineTrades;
    public decimal ProposalExpectancy => ProposalTrades == 0 ? 0m :
        ProposalNet / ProposalTrades;
    public decimal? BaselineSampledGiveback => Days.All(value =>
        value.Baseline.TotalSampledProfitGiveback.HasValue) ?
        Days.Sum(value => value.Baseline.TotalSampledProfitGiveback) : null;
    public decimal? ProposalSampledGiveback => Days.All(value =>
        value.Proposal.TotalSampledProfitGiveback.HasValue) ?
        Days.Sum(value => value.Proposal.TotalSampledProfitGiveback) : null;
    public decimal TrainingNetDifference => Days.Take(TrainingDays)
        .Sum(value => value.NetDifference);
    public decimal ValidationNetDifference => Days.Skip(TrainingDays)
        .Sum(value => value.NetDifference);
    public HistoricalSharedPortfolioPolicyMetrics TrainingBaseline =>
        Summarize(Days.Take(TrainingDays).Select(value => value.Baseline));
    public HistoricalSharedPortfolioPolicyMetrics TrainingProposal =>
        Summarize(Days.Take(TrainingDays).Select(value => value.Proposal));
    public HistoricalSharedPortfolioPolicyMetrics ValidationBaseline =>
        Summarize(Days.Skip(TrainingDays).Select(value => value.Baseline));
    public HistoricalSharedPortfolioPolicyMetrics ValidationProposal =>
        Summarize(Days.Skip(TrainingDays).Select(value => value.Proposal));
    public decimal BaselineWorstSessionDrawdown => Days.Count == 0 ? 0m :
        Days.Max(value => value.Baseline.MaximumDrawdown);
    public decimal ProposalWorstSessionDrawdown => Days.Count == 0 ? 0m :
        Days.Max(value => value.Proposal.MaximumDrawdown);
    public decimal BaselineBetweenDayDrawdown => BetweenDayDrawdown(Days
        .Select(value => value.Baseline.NetPnl));
    public decimal ProposalBetweenDayDrawdown => BetweenDayDrawdown(Days
        .Select(value => value.Proposal.NetPnl));
    public IReadOnlyList<HistoricalSharedPortfolioRegimeComparison> RegimeComparisons =>
        Days.GroupBy(value => value.DescriptiveRegime, StringComparer.Ordinal)
            .OrderBy(value => value.Key, StringComparer.Ordinal)
            .Select(group => new HistoricalSharedPortfolioRegimeComparison(
                group.Key, group.Count(),
                group.Sum(value => value.Baseline.NetPnl),
                group.Sum(value => value.Proposal.NetPnl),
                group.Sum(value => value.Baseline.CompletedPositions.Count),
                group.Sum(value => value.Proposal.CompletedPositions.Count)))
            .ToArray();

    private static decimal BetweenDayDrawdown(IEnumerable<decimal> dailyNet)
    {
        var equity = 0m;
        var peak = 0m;
        var worst = 0m;
        foreach (var net in dailyNet)
        {
            equity += net;
            peak = Math.Max(peak, equity);
            worst = Math.Max(worst, peak - equity);
        }
        return worst;
    }

    private static HistoricalSharedPortfolioPolicyMetrics Summarize(
        IEnumerable<HistoricalReplayResult> results)
    {
        var days = results.ToArray();
        var net = days.Sum(value => value.NetPnl);
        var trades = days.Sum(value => value.CompletedPositions.Count);
        return new(net, days.Sum(value => value.TotalCosts), trades,
            trades == 0 ? 0m : (decimal)days.Sum(value => value.Wins) / trades,
            trades == 0 ? 0m : net / trades,
            days.All(value => value.TotalSampledProfitGiveback.HasValue) ?
                days.Sum(value => value.TotalSampledProfitGiveback) : null,
            days.Length == 0 ? 0m : days.Max(value => value.MaximumDrawdown),
            BetweenDayDrawdown(days.Select(value => value.NetPnl)),
            days.Length == 0 ? 0m : days.Max(value =>
                value.PeakEntryCapitalExposure),
            days.Length == 0 ? 0 : days.Max(value =>
                value.PeakConcurrentPositions));
    }
}

// All markets on a date share a single event clock and position book. Dates
// are excluded only on input evidence, before policy decisions or P&L exist.
// This is still research-only until production entry/exit and shared-capital
// risk policies attest parity and the historical quote paths are adequate.
public static class HistoricalSharedPortfolioComparison
{
    public static HistoricalSharedPortfolioResult RunEligible(
        IReadOnlyList<HistoricalReplaySessionData> sessions,
        IReadOnlyList<string> requiredMarkets,
        Func<DateOnly, string, IHistoricalReplayPortfolioPolicy> baselineFactory,
        Func<DateOnly, string, IHistoricalReplayPortfolioPolicy> proposalFactory,
        HistoricalReplayParameters baselineParameters,
        HistoricalReplayParameters proposalParameters,
        IReadOnlyList<DateOnly>? requestedDates = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(baselineFactory);
        ArgumentNullException.ThrowIfNull(proposalFactory);
        baselineParameters.Validate();
        proposalParameters.Validate();
        var cohort = HistoricalReplayCohortSelector.Select(sessions,
            requiredMarkets, requestedDates,
            baselineParameters.AllowModeledHistoricalCosts && proposalParameters.AllowModeledHistoricalCosts);
        var days = new List<HistoricalSharedPortfolioDay>();
        var policyManifest = new List<object>();
        var allPoliciesVerified = true;
        string? baselineCodeVersion = null;
        string? proposalCodeVersion = null;
        foreach (var group in cohort.IncludedSessions.GroupBy(value => value.Date)
                     .OrderBy(value => value.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var marketSessions = group.OrderBy(value => value.IndexSymbol,
                StringComparer.Ordinal).ToArray();
            var start = marketSessions[0].StartUtc;
            var end = marketSessions[0].EndUtc;
            var baselineRules = marketSessions.ToDictionary(value =>
                    value.IndexSymbol,
                value => baselineFactory(value.Date, value.IndexSymbol),
                StringComparer.Ordinal);
            var proposalRules = marketSessions.ToDictionary(value =>
                    value.IndexSymbol,
                value => proposalFactory(value.Date, value.IndexSymbol),
                StringComparer.Ordinal);
            if (baselineRules.Values.Any(value => value is null) ||
                proposalRules.Values.Any(value => value is null))
                throw new ArgumentException("A market replay policy is missing.");
            var baseline = new HistoricalMultiMarketPortfolioPolicy(baselineRules);
            var proposal = new HistoricalMultiMarketPortfolioPolicy(proposalRules);
            var baselineVersion = baseline.CodeVersion;
            var proposalVersion = proposal.CodeVersion;
            var baselineSettings = baseline.ConfigurationManifest;
            var proposalSettings = proposal.ConfigurationManifest;
            var baselineParity = baseline.ProductionPolicyParityVerified;
            var proposalParity = proposal.ProductionPolicyParityVerified;
            if (string.IsNullOrWhiteSpace(baselineVersion) ||
                string.IsNullOrWhiteSpace(proposalVersion) ||
                string.IsNullOrWhiteSpace(baselineSettings) ||
                string.IsNullOrWhiteSpace(proposalSettings))
                throw new ArgumentException("Replay policy versions and settings are required.");
            baselineCodeVersion ??= baselineVersion;
            proposalCodeVersion ??= proposalVersion;
            if (baselineCodeVersion != baselineVersion ||
                proposalCodeVersion != proposalVersion)
                throw new InvalidOperationException(
                    "A replay policy code version changed across included dates.");
            allPoliciesVerified &= baselineParity && proposalParity;
            var bars = marketSessions.SelectMany(value => value.Bars).ToArray();
            var quotes = HistoricalQuotePaths.Merge(marketSessions.Select(value => value.Quotes));
            var chains = marketSessions.SelectMany(value => value.OptionChains)
                .ToArray();
            if (marketSessions.Any(value => value.OptionChains.Any(chain =>
                    chain.UnderlyingSymbol != value.IndexSymbol)))
                throw new ArgumentException(
                    "A replay option chain lacks its market identity.");
            var exchanges = marketSessions.SelectMany(value => value.Quotes
                    .Select(quote => (quote.Symbol, value.ExchangeCode)).Distinct())
                .GroupBy(value => value.Symbol, StringComparer.Ordinal)
                .ToDictionary(grouping => grouping.Key, grouping =>
                    grouping.Select(value => value.ExchangeCode).Distinct(
                        StringComparer.Ordinal).Single(), StringComparer.Ordinal);
            var original = HistoricalPortfolioReplay.Run(bars, quotes,
                baseline, baselineParameters, start, end, chains,
                contractExchanges: exchanges, cancellationToken: cancellationToken,
                sourceControls: marketSessions.SelectMany(value => value.ControlEvents).ToArray());
            var changed = HistoricalPortfolioReplay.Run(bars, quotes,
                proposal, proposalParameters, start, end, chains,
                contractExchanges: exchanges, cancellationToken: cancellationToken,
                sourceControls: marketSessions.SelectMany(value => value.ControlEvents).ToArray());
            if (baselineVersion != baseline.CodeVersion ||
                proposalVersion != proposal.CodeVersion ||
                baselineSettings != baseline.ConfigurationManifest ||
                proposalSettings != proposal.ConfigurationManifest ||
                baselineParity != baseline.ProductionPolicyParityVerified ||
                proposalParity != proposal.ProductionPolicyParityVerified)
                throw new InvalidOperationException(
                    "A replay policy identity or configuration changed during its session.");
            var regime = string.Join(" / ", marketSessions.Select(value =>
                value.IndexSymbol + ":" + HistoricalSessionRegimeClassifier.Classify(
                    value, HistoricalSessionCoverageEvaluator.Evaluate(value.Date,
                        value.Bars.Where(bar => bar.Symbol == value.IndexSymbol &&
                            bar.IntervalSeconds == 60)
                            .Select(bar => bar.OpenTimeUtc)))));
            days.Add(new(group.Key, original, changed)
            { DescriptiveRegime = regime });
            policyManifest.Add(new
            {
                Date = group.Key,
                BaselineVersion = baselineVersion,
                BaselineSettings = baselineSettings,
                ProposalVersion = proposalVersion,
                ProposalSettings = proposalSettings,
                BaselineParity = baselineParity,
                ProposalParity = proposalParity
            });
        }
        var dataFingerprint = cohort.IncludedSessions.Count == 0 ? string.Empty :
            HistoricalPortfolioComparison.Fingerprint(cohort.IncludedSessions
                .OrderBy(value => value.Date).ThenBy(value => value.IndexSymbol,
                    StringComparer.Ordinal));
        var runFingerprint = cohort.IncludedSessions.Count == 0 ? string.Empty :
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                dataFingerprint + "|" + HistoricalPortfolioReplay.EngineVersion +
                "|" + JsonSerializer.Serialize(policyManifest) +
                "|" + JsonSerializer.Serialize(baselineParameters) + "|" +
                JsonSerializer.Serialize(proposalParameters) + "|" +
                PaperTradingCostModel.ScheduleVersion + "|" +
                PaperTradingCostModel.BseScheduleVersion)));
        var trainCount = days.Count < 2 ? days.Count :
            Math.Clamp((int)Math.Floor(days.Count * .70m), 1, days.Count - 1);
        var gaps = new List<string>();
        if (days.Count == 0) gaps.Add("No eligible completed sessions were captured.");
        if (days.Count < 10) gaps.Add("Fewer than ten eligible trading dates.");
        if (!allPoliciesVerified)
            gaps.Add("Supported production entry/exit and market-scoped admission parity is not fully verified.");
        if (cohort.IncludedSessions.Any(value => !value.HistoricalChargesVerified))
            gaps.Add("Historical broker charges are not verified; both versions explicitly use the same versioned modeled paper-cost schedule.");
        if (days.Any(value => value.Baseline.MissingEntryQuoteCount > 0 ||
                value.Baseline.MissingExitQuoteCount > 0 ||
                value.Baseline.MissingAddOnQuoteCount > 0 ||
                value.Baseline.UnsupportedPartialCount > 0 ||
                value.Baseline.StaleQuoteCount > 0 ||
                value.Proposal.MissingEntryQuoteCount > 0 ||
                value.Proposal.MissingExitQuoteCount > 0 ||
                value.Proposal.MissingAddOnQuoteCount > 0 ||
                value.Proposal.UnsupportedPartialCount > 0 ||
                value.Proposal.StaleQuoteCount > 0))
            gaps.Add("At least one simulated decision lacks a fresh option quote or requests an unexecutable partial lot.");
        if (days.Skip(trainCount).Sum(value =>
                value.Baseline.CompletedPositions.Count) < 20 ||
            days.Skip(trainCount).Sum(value =>
                value.Proposal.CompletedPositions.Count) < 20)
            gaps.Add("The held-out dates contain fewer than twenty trades per version.");
        return new(cohort, days, dataFingerprint, runFingerprint,
            trainCount, days.Count - trainCount,
            gaps.Count == 0 ? "ResearchOnly" : "InsufficientCoverage", gaps);
    }
}
