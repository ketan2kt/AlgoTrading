using TradingSystem.Application.Execution;

namespace TradingSystem.UnitTests;

public sealed class HistoricalPortfolioComparisonTests
{
    private static readonly HistoricalReplayParameters Parameters =
        new(0.10m, 0.15m, 0m, 0m, 5, 0);
    private static readonly string[] ComparisonMarkets =
        ["NIFTY", "SENSEX", "BANKNIFTY"];

    [Fact]
    public void PairedVersionsUseSameInputsAndRemainChronological()
    {
        var first = Session(new DateOnly(2026, 10, 28), 115m);
        var second = Session(new DateOnly(2026, 10, 29), 110m);
        var result = HistoricalPortfolioComparison.Run([second, first],
            _ => new EntryPolicy("baseline-v1", true),
            _ => new EntryPolicy("proposal-v1", false), Parameters, Parameters);

        Assert.Equal(first.Date, result.Days[0].Date);
        Assert.Equal(second.Date, result.Days[1].Date);
        Assert.Equal(1, result.TrainingDays);
        Assert.Equal(1, result.ValidationDays);
        Assert.Equal(2, result.DateComparisons.Count);
        Assert.All(result.DateComparisons, date => Assert.Equal(1, date.Markets));
        Assert.Equal(result.Days.Sum(day => day.NetDifference),
            result.DateComparisons.Sum(day => day.NetDifference));
        Assert.Equal("InsufficientCoverage", result.EvidenceStatus);
        Assert.Contains(result.EvidenceGaps, gap => gap.Contains("option universe"));
        Assert.Contains(result.EvidenceGaps, gap => gap.Contains("strategy inputs"));
        Assert.Contains(result.EvidenceGaps, gap => gap.Contains("bid/offer"));
        Assert.Contains(result.EvidenceGaps, gap => gap.Contains("Date-effective"));
        Assert.Contains(result.EvidenceGaps, gap => gap.Contains("production-policy parity"));
        Assert.True(result.BaselineNet > 0m);
        Assert.Equal(0m, result.ProposalNet);
        Assert.Equal(result.Days.Sum(day => day.Baseline.Trades.Count),
            result.BaselineTradeCount);
        Assert.Equal(result.BaselineNet / result.BaselineTradeCount,
            result.BaselineExpectancy);
        Assert.Equal(0m, result.ProposalBetweenDayDrawdown);
        Assert.Equal(result.Days.Sum(day => day.Baseline.TotalCosts),
            result.BaselineTotalCosts);
        var unknown = Assert.Single(result.RegimeComparisons);
        Assert.Equal("Unknown", unknown.Regime);
        Assert.Equal(2, unknown.Sessions);
        Assert.Equal(result.BaselineNet, unknown.BaselineNet);
        Assert.NotNull(result.BaselineSampledGiveback);
        var reordered = HistoricalPortfolioComparison.Run([first, second],
            _ => new EntryPolicy("baseline-v1", true),
            _ => new EntryPolicy("proposal-v1", false), Parameters, Parameters);
        Assert.Equal(result.DataFingerprint, reordered.DataFingerprint);
        var sameCloseTimeBars = first.Bars.Concat([
            new HistoricalReplayBar(first.StartUtc, 300, 22000m, 22002m,
                21998m, 22001m) { Symbol = "NIFTY" },
            new HistoricalReplayBar(first.StartUtc.AddMinutes(4), 60,
                22001m, 22003m, 22000m, 22002m) { Symbol = "NIFTY" }
        ]).ToArray();
        var tiedOrderA = HistoricalPortfolioComparison.Run([
            first with { Bars = sameCloseTimeBars }, second],
            _ => new EntryPolicy("baseline-v1", false),
            _ => new EntryPolicy("proposal-v1", false), Parameters, Parameters);
        var tiedOrderB = HistoricalPortfolioComparison.Run([
            first with { Bars = sameCloseTimeBars.Reverse().ToArray() }, second],
            _ => new EntryPolicy("baseline-v1", false),
            _ => new EntryPolicy("proposal-v1", false), Parameters, Parameters);
        Assert.Equal(tiedOrderA.DataFingerprint, tiedOrderB.DataFingerprint);
        var changed = HistoricalPortfolioComparison.Run([Session(first.Date, 114m), second],
            _ => new EntryPolicy("baseline-v1", true),
            _ => new EntryPolicy("proposal-v1", false), Parameters, Parameters);
        Assert.NotEqual(result.DataFingerprint, changed.DataFingerprint);
        var metadataChanged = HistoricalPortfolioComparison.Run([
            first with { PolicyInputManifest = "prior=21950|lot=100" }, second],
            _ => new EntryPolicy("baseline-v1", true),
            _ => new EntryPolicy("proposal-v1", false), Parameters, Parameters);
        Assert.NotEqual(result.DataFingerprint, metadataChanged.DataFingerprint);
        var universeChanged = HistoricalPortfolioComparison.Run([
            first with { EligibleOptionUniverseComplete = true }, second],
            _ => new EntryPolicy("baseline-v1", true),
            _ => new EntryPolicy("proposal-v1", false), Parameters, Parameters);
        Assert.NotEqual(result.DataFingerprint, universeChanged.DataFingerprint);
        var oiChanged = HistoricalPortfolioComparison.Run([
            first with { Bars = first.Bars.Select(bar => bar with
                { OpenInterest = 100m }).ToArray() }, second],
            _ => new EntryPolicy("baseline-v1", true),
            _ => new EntryPolicy("proposal-v1", false), Parameters, Parameters);
        Assert.NotEqual(result.DataFingerprint, oiChanged.DataFingerprint);
        var optionOiChanged = HistoricalPortfolioComparison.Run([
            first with { Quotes = first.Quotes.Select(quote => quote with
                { OpenInterest = 900m, Volume = 45 }).ToArray() }, second],
            _ => new EntryPolicy("baseline-v1", true),
            _ => new EntryPolicy("proposal-v1", false), Parameters, Parameters);
        Assert.NotEqual(result.DataFingerprint, optionOiChanged.DataFingerprint);
        var spreadChanged = HistoricalPortfolioComparison.Run([
            first with { Quotes = first.Quotes.Select(quote => quote with
                { BidPrice = quote.LastPrice - 1m,
                  OfferPrice = quote.LastPrice + 1m }).ToArray() }, second],
            _ => new EntryPolicy("baseline-v1", true),
            _ => new EntryPolicy("proposal-v1", false), Parameters, Parameters);
        Assert.NotEqual(result.DataFingerprint, spreadChanged.DataFingerprint);
        var depthSizeChanged = HistoricalPortfolioComparison.Run([
            first with { Quotes = first.Quotes.Select(quote => quote with
                { BidQuantity = 10, OfferQuantity = 20 }).ToArray() }, second],
            _ => new EntryPolicy("baseline-v1", true),
            _ => new EntryPolicy("proposal-v1", false), Parameters, Parameters);
        Assert.NotEqual(result.DataFingerprint, depthSizeChanged.DataFingerprint);
        var sidesCertified = HistoricalPortfolioComparison.Run([
            first with { ExecutableQuoteSidesComplete = true }, second],
            _ => new EntryPolicy("baseline-v1", true),
            _ => new EntryPolicy("proposal-v1", false), Parameters, Parameters);
        Assert.NotEqual(result.DataFingerprint, sidesCertified.DataFingerprint);
        Assert.Contains(sidesCertified.EvidenceGaps, gap => gap.Contains("bid/offer"));
        var chargesCertified = HistoricalPortfolioComparison.Run([
            first with { HistoricalChargesVerified = true }, second],
            _ => new EntryPolicy("baseline-v1", true),
            _ => new EntryPolicy("proposal-v1", false), Parameters, Parameters);
        Assert.NotEqual(result.DataFingerprint, chargesCertified.DataFingerprint);
        Assert.Contains(chargesCertified.EvidenceGaps, gap =>
            gap.Contains("Date-effective"));
        var changedAssumptions = HistoricalPortfolioComparison.Run([first, second],
            _ => new EntryPolicy("baseline-v1", true),
            _ => new EntryPolicy("proposal-v1", false), Parameters,
            Parameters with { ExitSlippagePercent = 0.01m });
        Assert.Equal(result.DataFingerprint, changedAssumptions.DataFingerprint);
        Assert.NotEqual(result.RunFingerprint, changedAssumptions.RunFingerprint);
        var bseCostStudy = HistoricalPortfolioComparison.Run([
            first with { ExchangeCode = "BSE" },
            second with { ExchangeCode = "BSE" }],
            _ => new EntryPolicy("baseline-v1", true),
            _ => new EntryPolicy("proposal-v1", false), Parameters, Parameters);
        Assert.True(bseCostStudy.BaselineNet > result.BaselineNet);
        Assert.NotEqual(result.DataFingerprint, bseCostStudy.DataFingerprint);
        Assert.NotEqual(result.RunFingerprint, bseCostStudy.RunFingerprint);
        var changedPolicySettings = HistoricalPortfolioComparison.Run([first, second],
            _ => new EntryPolicy("baseline-v1", true, "threshold=0.7"),
            _ => new EntryPolicy("proposal-v1", false), Parameters, Parameters);
        Assert.Equal(result.DataFingerprint, changedPolicySettings.DataFingerprint);
        Assert.NotEqual(result.RunFingerprint, changedPolicySettings.RunFingerprint);
        Assert.Contains(result.EvidenceGaps, gap =>
            gap.Contains("policy-configuration manifest"));
        var parityClaimChanged = HistoricalPortfolioComparison.Run([first, second],
            _ => new EntryPolicy("baseline-v1", true, parityVerified: true),
            _ => new EntryPolicy("proposal-v1", false), Parameters, Parameters);
        Assert.Equal(result.DataFingerprint, parityClaimChanged.DataFingerprint);
        Assert.NotEqual(result.RunFingerprint, parityClaimChanged.RunFingerprint);
        Assert.Contains(parityClaimChanged.EvidenceGaps, gap =>
            gap.Contains("production-policy parity"));
    }

    [Fact]
    public void DuplicateSessionIsRejected()
    {
        var session = Session(new DateOnly(2026, 10, 29), 115m);
        Assert.Throws<ArgumentException>(() => HistoricalPortfolioComparison.Run(
            [session, session], _ => new EntryPolicy("baseline-v1", true),
            _ => new EntryPolicy("proposal-v1", false), Parameters, Parameters));
    }

    [Fact]
    public void ThreeMarketsOnEachDateRemainInTheSameTimeSplit()
    {
        var dates = new[] { new DateOnly(2026, 10, 28),
            new DateOnly(2026, 10, 29) };
        var sessions = dates.SelectMany(date =>
            ComparisonMarkets.Select(symbol =>
            Session(date, 115m) with
            {
                IndexSymbol = symbol,
                ExchangeCode = symbol == "SENSEX" ? "BSE" : "NSE",
                Bars = Session(date, 115m).Bars.Select(bar => bar with
                    { Symbol = symbol }).ToArray()
            })).Reverse().ToArray();

        var selectedMarkets = new List<string>();
        var result = HistoricalPortfolioComparison.Run(sessions,
            (_, market) =>
            {
                selectedMarkets.Add(market);
                return new EntryPolicy("baseline-v1", false, market);
            },
            (_, market) => new EntryPolicy("proposal-v1", false, market),
            Parameters, Parameters);

        Assert.Equal(6, result.Days.Count);
        Assert.Equal(6, selectedMarkets.Count);
        Assert.Equal(ComparisonMarkets.OrderBy(value => value, StringComparer.Ordinal),
            selectedMarkets.Take(3));
        Assert.Equal(1, result.TrainingDays);
        Assert.Equal(1, result.ValidationDays);
        Assert.All(result.DateComparisons, date => Assert.Equal(3, date.Markets));
        Assert.All(result.Days.Take(3), day => Assert.Equal(dates[0], day.Date));
        Assert.All(result.Days.Skip(3), day => Assert.Equal(dates[1], day.Date));
        Assert.Equal(ComparisonMarkets.OrderBy(value => value, StringComparer.Ordinal),
            result.Days.Take(3).Select(day => day.IndexSymbol));
        Assert.Contains(result.EvidenceGaps, gap =>
            gap.Contains("trading days", StringComparison.Ordinal));
        Assert.Contains(result.EvidenceGaps, gap =>
            gap.Contains("independent replay state", StringComparison.Ordinal));
        var reordered = HistoricalPortfolioComparison.Run(sessions.Reverse().ToArray(),
            _ => new EntryPolicy("baseline-v1", false),
            _ => new EntryPolicy("proposal-v1", false), Parameters, Parameters);
        Assert.Equal(result.DataFingerprint, reordered.DataFingerprint);
    }

    [Fact]
    public void MutationOfPolicyConfigurationDuringReplayIsRejected()
    {
        var session = Session(new DateOnly(2026, 10, 29), 115m);
        Assert.Throws<InvalidOperationException>(() =>
            HistoricalPortfolioComparison.Run([session],
                _ => new MutatingPolicy(),
                _ => new EntryPolicy("proposal-v1", false, "stable"),
                Parameters, Parameters));
    }

    [Fact]
    public void CompleteUnderlyingAloneCannotCertifyReplayEvidence()
    {
        var sessions = Enumerable.Range(0, 10).Select(index =>
            Session(new DateOnly(2026, 10, 1).AddDays(index), 115m) with
            { RequiredInputsComplete = true }).ToArray();
        var result = HistoricalPortfolioComparison.Run(sessions,
            _ => new EntryPolicy("baseline-v1", true),
            _ => new EntryPolicy("proposal-v1", true), Parameters, Parameters);

        Assert.Equal("InsufficientCoverage", result.EvidenceStatus);
        Assert.All(sessions, session => Assert.False(
            session.EligibleOptionUniverseComplete ||
            session.StrategyInputsComplete));
    }

    [Fact]
    public void BetweenDayDrawdownUsesCumulativeDayClosesNotWorstSessionMark()
    {
        var result = HistoricalPortfolioComparison.Run([
            Session(new DateOnly(2026, 10, 28), 115m),
            Session(new DateOnly(2026, 10, 29), 80m)
        ], _ => new EntryPolicy("baseline-v1", true),
            _ => new EntryPolicy("proposal-v1", false), Parameters, Parameters);

        Assert.True(result.BaselineBetweenDayDrawdown > 0m);
        Assert.Equal(0m, result.ProposalBetweenDayDrawdown);
        Assert.Equal(-result.Days[1].Baseline.NetPnl,
            result.BaselineBetweenDayDrawdown);
    }

    [Fact]
    public void BetweenDayDrawdownDoesNotCountTemporaryCrossMarketOrdering()
    {
        var date = new DateOnly(2026, 10, 29);
        HistoricalReplayResult Result(decimal net) =>
            new([], 0, 0, 0, 0, net, 0m, "modeled");
        HistoricalPortfolioComparisonDay Day(string market, decimal net) =>
            new(date, Result(net), Result(net), 0m,
                new HistoricalIndexMinuteCoverage(0, 0, 0, 0), true)
            { IndexSymbol = market };
        var comparison = new HistoricalPortfolioComparisonResult("data", "run",
            "baseline", "proposal",
            [Day("NIFTY", 100m), Day("SENSEX", -100m)],
            1, 0, 0m, 0m, "InsufficientCoverage");

        Assert.Equal(0m, comparison.BaselineBetweenDayDrawdown);
        Assert.Equal(0m, comparison.ProposalBetweenDayDrawdown);
        Assert.Equal(0m, Assert.Single(comparison.DateComparisons).BaselineNet);
    }

    private static HistoricalReplaySessionData Session(DateOnly date, decimal exitPrice)
    {
        var start = new DateTimeOffset(date.ToDateTime(new TimeOnly(9, 15)),
            TimeSpan.FromHours(5.5)).ToUniversalTime();
        return new(date, "NIFTY", [new(start, 60, 22000m, 22001m, 21999m, 22000m)
            { Symbol = "NIFTY" }], [
                new("NIFTY22000CE", start.AddMinutes(2), 100m),
                new("NIFTY22000CE", start.AddMinutes(3), 120m),
                new("NIFTY22000CE", start.AddMinutes(4), exitPrice)
            ], start, start.AddMinutes(5))
        { PolicyInputManifest = "prior=21950|lot=75", RequiredInputsComplete = false };
    }

    private sealed class EntryPolicy(string codeVersion, bool enter,
        string configurationManifest = "", bool parityVerified = false)
        : IHistoricalReplayPortfolioPolicy
    {
        public string CodeVersion => codeVersion;
        public string ConfigurationManifest => configurationManifest;
        public bool ProductionPolicyParityVerified => parityVerified;
        public IReadOnlyList<HistoricalReplayCandidate> Evaluate(
            HistoricalReplayPortfolioFrame frame) => enter &&
            frame.OpenPosition is null && frame.ClosedTrades.Count == 0 &&
            frame.Market.AvailableAtUtc == frame.Market.CompletedBars[0].AvailableAtUtc
                ? [new("NIFTY22000CE", frame.Market.AvailableAtUtc,
                    codeVersion, 0.8m, 75)] : [];
    }

    private sealed class MutatingPolicy : IHistoricalReplayPortfolioPolicy
    {
        private int evaluations;
        public string CodeVersion => "mutable-v1";
        public string ConfigurationManifest => $"evaluations={evaluations}";
        public IReadOnlyList<HistoricalReplayCandidate> Evaluate(
            HistoricalReplayPortfolioFrame frame)
        {
            evaluations++;
            return [];
        }
    }
}
