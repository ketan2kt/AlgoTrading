using TradingSystem.Application.Execution;
using TradingSystem.Application.Regime;
using TradingSystem.Application.Strategies;
using TradingSystem.Application.Broker;
using TradingSystem.Application.Risk;

namespace TradingSystem.UnitTests;

public sealed class HistoricalNiftySignalPolicyTests
{
    private static readonly DateOnly Session = new(2026, 10, 29);
    private static readonly DateTimeOffset Start = new(2026, 10, 29, 3, 45, 0,
        TimeSpan.Zero);

    [Fact]
    public void CompletedSpotAndFuturesBarsFeedTheProductionStrategyInterface()
    {
        var strategy = new CapturingStrategy();
        var policy = NewPolicy(strategy);
        var bars = Enumerable.Range(0, 15).SelectMany(minute => new[]
        {
            new HistoricalReplayBar(Start.AddMinutes(minute), 60,
                22000m + minute, 22002m + minute, 21998m + minute,
                22001m + minute) { Symbol = "NIFTY" },
            new HistoricalReplayBar(Start.AddMinutes(minute), 60,
                22010m + minute, 22012m + minute, 22008m + minute,
                22011m + minute) { Symbol = "NIFTY-FUT", Volume = 100 }
        }).ToArray();
        var market = new HistoricalReplayFrame(Start.AddMinutes(15), bars,
            new Dictionary<string, HistoricalReplayQuote>());

        var candidates = policy.Evaluate(new HistoricalReplayPortfolioFrame(market,
            null, []));

        Assert.Empty(candidates);
        Assert.NotNull(strategy.LastContext);
        Assert.True(strategy.LastContext.RelaxedNiftyEntries);
        Assert.Contains(NiftyEntryTrialProfile.Version, policy.ConfigurationManifest);
        Assert.Equal(15, strategy.LastContext.RecentCandles.Count);
        Assert.Equal(1m, strategy.LastContext.RelativeVolume);
        Assert.Equal(22015m, strategy.LastContext.CurrentPrice);
        Assert.Equal(22016m, strategy.LastContext.OpeningRangeHigh);
        Assert.Equal(21998m, strategy.LastContext.OpeningRangeLow);
        Assert.InRange(strategy.LastContext.Vwap, 22007.33m, 22007.34m);
        Assert.InRange(strategy.LastContext.AtrPercent, 0.018m, 0.019m);
        Assert.Equal(Start.AddMinutes(15), strategy.LastContext.ObservedAtUtc);
    }

    [Fact]
    public void CooldownUsesLatestCompletedSignalTimeNotTheLastExitTime()
    {
        var strategy = new CapturingStrategy();
        var policy = NewPolicy(strategy);
        var bars = Enumerable.Range(0, 15).SelectMany(minute => new[]
        {
            new HistoricalReplayBar(Start.AddMinutes(minute), 60, 22000m + minute,
                22002m + minute, 21998m + minute, 22001m + minute) { Symbol = "NIFTY" },
            new HistoricalReplayBar(Start.AddMinutes(minute), 60, 22010m + minute,
                22012m + minute, 22008m + minute, 22011m + minute)
                { Symbol = "NIFTY-FUT", Volume = 100 }
        }).ToArray();
        HistoricalReplayFill Fill(int signalMinute, int exitMinute) => new("NIFTY22000CE",
            "sample", Start.AddMinutes(signalMinute), Start.AddMinutes(signalMinute),
            Start.AddMinutes(exitMinute), 100m, 110m, 75, 750m, 50m, 700m, "TargetHit");
        var frame = new HistoricalReplayPortfolioFrame(new(Start.AddMinutes(15), bars,
            new Dictionary<string, HistoricalReplayQuote>()), null,
            [Fill(2, 14), Fill(10, 13)]);
        policy.Evaluate(frame);
        Assert.Equal(Start.AddMinutes(10), strategy.LastContext!.LastSignalAtUtc);
    }

    [Fact]
    public void MissingFuturesConfirmationFailsClosedBeforeStrategyInvocation()
    {
        var strategy = new CapturingStrategy();
        var bars = Enumerable.Range(0, 15).Select(minute =>
            new HistoricalReplayBar(Start.AddMinutes(minute), 60,
                22000m, 22002m, 21998m, 22001m) { Symbol = "NIFTY" }).ToArray();
        var market = new HistoricalReplayFrame(Start.AddMinutes(15), bars,
            new Dictionary<string, HistoricalReplayQuote>());

        Assert.Empty(NewPolicy(strategy).Evaluate(new HistoricalReplayPortfolioFrame(
            market, null, [])));
        Assert.Null(strategy.LastContext);
    }

    [Fact]
    public void FutureGapsDoNotCreateReplayOnlyStrategyRejections()
    {
        var bars = Enumerable.Range(0, 16).SelectMany(minute => new[]
        {
            new HistoricalReplayBar(Start.AddMinutes(minute), 60,
                22000m, 22002m, 21998m, 22001m) { Symbol = "NIFTY" },
            new HistoricalReplayBar(Start.AddMinutes(minute), 60,
                22010m, 22012m, 22008m, 22011m)
            { Symbol = "NIFTY-FUT", Volume = 100 }
        }).ToArray();
        foreach (var missingMinute in new[] { 15, 14 })
        {
            var strategy = new CapturingStrategy();
            var frame = new HistoricalReplayFrame(Start.AddMinutes(16),
                bars.Where(bar => bar.Symbol != "NIFTY-FUT" ||
                    bar.OpenTimeUtc != Start.AddMinutes(missingMinute)).ToArray(),
                new Dictionary<string, HistoricalReplayQuote>());
            Assert.Empty(NewPolicy(strategy).Evaluate(new HistoricalReplayPortfolioFrame(
                frame, null, [])));
            Assert.NotNull(strategy.LastContext);
        }
    }

    [Fact]
    public void LaterObservedOptionQuoteRunsTheSharedAdmissionPolicy()
    {
        var policy = NewPolicy(new CapturingStrategy(), AdmissionSettings());
        var pending = new HistoricalReplayCandidate("NIFTY22000CE", Start,
            "test", .8m, 150)
        {
            DeferredNiftyAdmission = new NiftyOptionAdmissionInput(
                new GrowwQuote(0m, null, null, null, null, null,
                    null, null, null),
                true, 1.5m, 500m, 100m, 100m,
                Session, Session.AddDays(2), 10m, 1m,
                .05m, 75, 2,
                new ProfessionalTradeAssessmentInput("nifty",
                    ProfessionalTradeRegime.Directional, .8m, 1m,
                    2m, null, null, null, 0, null, null,
                    0m, false, 0, true),
                new PaperPortfolioRiskInput(0m, 0m, 75, 2,
                    100_000m, 1_000_000m, 0m, 0m, 0m,
                    100_000m, 0, 8, false, true, true))
        };
        var observed = new HistoricalReplayQuote("NIFTY22000CE",
            Start.AddSeconds(3), 100m)
        {
            BidPrice = 100m, OfferPrice = 101m,
            Volume = 10_000, OpenInterest = 50_000m
        };
        var frame = new HistoricalReplayPortfolioFrame(
            new HistoricalReplayFrame(observed.AvailableAtUtc, [],
                new Dictionary<string, HistoricalReplayQuote>
                { [observed.Symbol] = observed }), null, []);

        var admitted = policy.AssessDeferredEntry(frame, pending, observed);

        Assert.NotNull(admitted);
        Assert.Equal(observed.AvailableAtUtc, admitted.DecidedAtUtc);
        Assert.Equal(150, admitted.Quantity);
        Assert.True(admitted.PlannedStopPrice < 101m);
        Assert.True(admitted.PlannedTargetPrice > 101m);
        Assert.Null(admitted.DeferredNiftyAdmission);
    }

    [Fact]
    public void StrategyOptionChangesAlterReplayConfigurationFingerprint()
    {
        var baseline = NewPolicy(new MomentumExpansionStrategy(new()));
        var proposed = NewPolicy(new MomentumExpansionStrategy(
            new PriceActionStrategyOptions { MinimumScore = .90m }));

        Assert.NotEqual(baseline.ConfigurationManifest,
            proposed.ConfigurationManifest);
    }

    private static HistoricalNiftySignalPolicy NewPolicy(ITradingStrategy strategy,
        NiftyOptionAdmissionSettings? admission = null) =>
        new(Guid.NewGuid(), "NIFTY", "NIFTY-FUT", Session, 21950m,
            new TimeOnly(9, 30), 15, 15, 60, 1,
            Array.Empty<NiftyOptionContractCandidate>(),
            new NiftyOptionSelectionOptions(),
            new MarketRegimeEngine(new MarketRegimeOptions()), strategy,
            admission);

    private static NiftyOptionAdmissionSettings AdmissionSettings() => new(
        true, 1.5m, 500m, 100m, 100m, 10m, 1m,
        8, 100_000m, 1_000_000m, 100_000m,
        new TimeOnly(9, 15), new TimeOnly(15, 0),
        new TimeOnly(15, 15), 5, 10,
        true, .35m, 1m, .6m);

    private sealed class CapturingStrategy : ITradingStrategy
    {
        public string StrategyId => "capture";
        public string Version => "1";
        public StrategyEvaluationContext? LastContext { get; private set; }
        public StrategySignal? Evaluate(StrategyEvaluationContext context) =>
            EvaluateDetailed(context).Signal;
        public StrategyEvaluationResult EvaluateDetailed(StrategyEvaluationContext context)
        {
            LastContext = context;
            return new(null, []);
        }
    }
}
