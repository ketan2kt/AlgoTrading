using TradingSystem.Application.Execution;

namespace TradingSystem.UnitTests;

public sealed class HistoricalReplayCohortSelectorTests
{
    [Fact]
    public void SpotLtpDoesNotRequireAnOptionOrderBook()
    {
        var session = Session(new DateOnly(2026, 11, 3));
        session = session with { Quotes = [.. session.Quotes,
            new(session.IndexSymbol, session.StartUtc, 22000m)] };
        var cohort = HistoricalReplayCohortSelector.Select([session], ["NIFTY"]);
        Assert.Single(cohort.IncludedSessions);
        Assert.Empty(cohort.ExcludedDates);
    }

    [Fact]
    public void IncompleteOldDateIsExcludedBeforeBothPoliciesRun()
    {
        var old = Session(new DateOnly(2026, 10, 26)) with
        { ExecutableQuoteSidesComplete = false };
        var current = Session(new DateOnly(2026, 11, 3));
        var evaluated = new List<DateOnly>();
        IHistoricalReplayPortfolioPolicy Factory(DateOnly date, string _)
        {
            evaluated.Add(date);
            return new EmptyPolicy();
        }

        var result = HistoricalPortfolioComparison.RunEligible(
            [old, current], ["NIFTY"], Factory, Factory,
            HistoricalReplayParameters.Default,
            HistoricalReplayParameters.Default);

        Assert.Equal(current.Date,
            Assert.Single(result.Cohort.IncludedSessions).Date);
        var excluded = Assert.Single(result.Cohort.ExcludedDates);
        Assert.Equal(old.Date, excluded.Date);
        Assert.Contains(excluded.Reasons, value => value.Contains("bid/offer"));
        Assert.All(evaluated, value => Assert.Equal(current.Date, value));
        Assert.Equal(current.Date,
            Assert.Single(result.Comparison!.Days).Date);
    }

    [Fact]
    public void MissingMarketExcludesWholeDateWithoutOutcomeSelection()
    {
        var date = new DateOnly(2026, 11, 3);
        var nifty = Session(date);
        var sensex = Session(date) with { IndexSymbol = "SENSEX" };

        var result = HistoricalReplayCohortSelector.Select(
            [nifty, sensex], ["NIFTY", "SENSEX", "BANKNIFTY"]);

        Assert.Empty(result.IncludedSessions);
        var exclusion = Assert.Single(result.ExcludedDates);
        Assert.Equal(date, exclusion.Date);
        Assert.Contains(exclusion.Reasons, value =>
            value.Contains("BANKNIFTY: session archive missing"));
    }

    [Fact]
    public void NoEligibleDatesReturnsExclusionsWithoutInventingComparison()
    {
        var old = Session(new DateOnly(2026, 10, 26)) with
        { HistoricalChargesVerified = false };
        var result = HistoricalPortfolioComparison.RunEligible([old],
            ["NIFTY"], (_, _) => throw new InvalidOperationException(
                "An excluded date must never evaluate a policy."),
            (_, _) => throw new InvalidOperationException(),
            HistoricalReplayParameters.Default,
            HistoricalReplayParameters.Default);

        Assert.Null(result.Comparison);
        Assert.Contains(Assert.Single(result.Cohort.ExcludedDates).Reasons,
            value => value.Contains("charges unverified"));
    }

    [Fact]
    public void RequestedDateWithoutAnyArchiveIsExplicitlyExcluded()
    {
        var missing = new DateOnly(2026, 11, 2);
        var present = new DateOnly(2026, 11, 3);
        var result = HistoricalReplayCohortSelector.Select([Session(present)],
            ["NIFTY"], [missing, present]);

        Assert.Equal(present, Assert.Single(result.IncludedSessions).Date);
        var excluded = Assert.Single(result.ExcludedDates);
        Assert.Equal(missing, excluded.Date);
        Assert.Contains("NIFTY: session archive missing", excluded.Reasons);
    }

    [Fact]
    public void ModeledCostsRequireAnExplicitAssumptionAndDoNotBypassMissingMarketData()
    {
        var session = Session(new(2026, 11, 3)) with { HistoricalChargesVerified = false };
        Assert.Empty(HistoricalReplayCohortSelector.Select([session], ["NIFTY"]).IncludedDates);
        Assert.Single(HistoricalReplayCohortSelector.Select([session], ["NIFTY"],
            allowModeledHistoricalCosts: true).IncludedDates);
        Assert.Empty(HistoricalReplayCohortSelector.Select([session with { ExecutableQuoteSidesComplete = false }],
            ["NIFTY"], allowModeledHistoricalCosts: true).IncludedDates);
    }

    [Fact]
    public void ConfirmationFutureQuoteDoesNotNeedOptionExecutionSides()
    {
        var session = Session(new(2026, 11, 3));
        var quote = new HistoricalReplayQuote("NIFTY-FUT", session.StartUtc.AddSeconds(1), 22500m);
        Assert.Single(HistoricalReplayCohortSelector.Select([session with { Quotes = session.Quotes.Append(quote).ToArray() }],
            ["NIFTY"]).IncludedDates);
    }

    [Fact]
    public void InvalidSessionIsRejectedBeforeRequestedDateValidation()
    {
        Assert.Throws<ArgumentException>(() =>
            HistoricalReplayCohortSelector.Select([null!], ["NIFTY"],
                [new DateOnly(2026, 11, 3)]));
    }

    private static HistoricalReplaySessionData Session(DateOnly date)
    {
        var start = new DateTimeOffset(date.ToDateTime(new TimeOnly(9, 15)),
            TimeSpan.FromHours(5.5)).ToUniversalTime();
        var bars = Enumerable.Range(0, 375).Select(minute =>
            new HistoricalReplayBar(start.AddMinutes(minute), 60,
                22000m, 22001m, 21999m, 22000m)
            { Symbol = "NIFTY" }).ToArray();
        return new(date, "NIFTY", bars, [
            new HistoricalReplayQuote("NIFTY22000CE", start.AddMinutes(2), 100m)
            { BidPrice = 99m, OfferPrice = 101m },
            new HistoricalReplayQuote("NIFTY22000CE", start.AddMinutes(3), 101m)
            { BidPrice = 100m, OfferPrice = 102m }
        ], start, start.AddMinutes(375))
        {
            RequiredInputsComplete = true,
            EligibleOptionUniverseComplete = true,
            StrategyInputsComplete = true,
            ExecutableQuoteSidesComplete = true,
            HistoricalChargesVerified = true,
            PolicyInputManifest = "frozen-test-input-v1"
        };
    }

    private sealed class EmptyPolicy : IHistoricalReplayPortfolioPolicy
    {
        public string CodeVersion => "empty-policy-v1";
        public string ConfigurationManifest => "empty-test-v1";
        public IReadOnlyList<HistoricalReplayCandidate> Evaluate(
            HistoricalReplayPortfolioFrame _) => [];
    }
}
