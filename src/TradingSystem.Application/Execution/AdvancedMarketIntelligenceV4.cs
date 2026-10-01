using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Execution;

public sealed record OptionResearchPointV4(decimal Strike, bool IsCall, decimal LastPrice,
    decimal OpenInterest, decimal Volume, decimal? Delta, decimal? Gamma, decimal? Theta,
    decimal? Vega, decimal? ImpliedVolatility);

public sealed record ScheduledMarketEventV4(string Name, DateTimeOffset StartsAtUtc,
    int RiskWindowMinutes, string Severity);

public sealed record CapabilityStatusV4(string Capability, string Status, string Evidence);
public sealed record VolatilityContextV4(string State, decimal CurrentAtrPercent,
    decimal BaselineAtrPercent, decimal Percentile, decimal ExpansionRatio);
public sealed record OptionContextV4(bool Available, int Contracts, decimal PutCallOiRatio,
    decimal AverageImpliedVolatility, decimal IvSkew, int LiquidContracts,
    string SpreadStatus, string Evidence);
public sealed record CrossMarketContextV4(bool Available, decimal ReturnCorrelation,
    string Alignment, string Evidence);
public sealed record PriceLevelV4(string Kind, decimal Price, decimal Strength, string Evidence);
public sealed record VolumeProfileV4(bool Available, decimal? PointOfControl,
    decimal? ValueAreaLow, decimal? ValueAreaHigh, decimal? AnchoredVwap, string Evidence);
public sealed record GenericSetupLifecycleV4(string Phase, Direction? Direction,
    decimal ExtensionAtr, int AgeCandles, string Evidence);
public sealed record MetaLabelV4(decimal AcceptanceProbability, string Verdict,
    IReadOnlyList<string> Evidence);
public sealed record UncertaintyV4(decimal Score, string Verdict, IReadOnlyList<string> Reasons);
public sealed record PortfolioCoordinationV4(string Verdict, decimal Correlation,
    string Evidence);
public sealed record AdaptiveRiskV4(decimal ResearchSizeMultiplier, string Verdict,
    IReadOnlyList<string> Evidence);
public sealed record DriftDetectionV4(string Status, decimal VolatilityRatio,
    decimal RegimeDistributionShift, IReadOnlyList<string> Evidence);
public sealed record ExperimentDefinitionV4(string Id, string Hypothesis, string Status,
    int MinimumCandidates, int MinimumSessions, string SuccessCriteria,
    string RollbackCriteria);

public sealed record AdvancedMarketIntelligenceV4(string Version,
    VolatilityContextV4 Volatility, OptionContextV4 Options,
    CrossMarketContextV4 CrossMarket, IReadOnlyList<CapabilityStatusV4> Capabilities,
    IReadOnlyList<PriceLevelV4> Levels, VolumeProfileV4 VolumeProfile,
    GenericSetupLifecycleV4 SetupLifecycle, MetaLabelV4 MetaLabel,
    UncertaintyV4 Uncertainty, PortfolioCoordinationV4 Portfolio,
    AdaptiveRiskV4 AdaptiveRisk, DriftDetectionV4 Drift,
    IReadOnlyList<ExperimentDefinitionV4> Experiments,
    ResearchOperationsV5? Operations = null);

public static class AdvancedMarketIntelligenceAnalyzerV4
{
    public static AdvancedMarketIntelligenceV4 Analyze(
        IReadOnlyList<StrategyPriceBar> currentSession,
        IReadOnlyList<StrategyPriceBar> history,
        IReadOnlyList<StrategyPriceBar> peerSession,
        IReadOnlyList<OptionResearchPointV4> options,
        IReadOnlyList<ScheduledMarketEventV4> events,
        IReadOnlyList<DecisionFeatureSnapshotV2> currentDecisions,
        IReadOnlyList<DecisionFeatureSnapshotV2> baselineDecisions,
        IReadOnlyList<ResearchRecommendationV3> recommendations,
        DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(currentSession);
        ArgumentNullException.ThrowIfNull(history);
        var session = currentSession.OrderBy(value => value.OpenTimeUtc).ToArray();
        var historical = history.OrderBy(value => value.OpenTimeUtc).ToArray();
        var volatility = Volatility(session, historical);
        var optionContext = Options(options);
        var cross = CrossMarket(session, peerSession);
        var capabilities = new List<CapabilityStatusV4>
        {
            new("Order-book spread and depth", "Partial",
                "Live trade pricing validates executable bid/offer; historical option-chain snapshots do not contain full depth."),
            new("Options Greeks and IV surface", optionContext.Available ? "Ready" : "Unavailable",
                optionContext.Evidence),
            new("Cross-index confirmation", cross.Available ? "Ready" : "Unavailable", cross.Evidence),
            new("Constituent breadth", "Unavailable",
                "No licensed constituent breadth feed is configured; no value is inferred or scraped."),
            new("Scheduled event risk", events.Count > 0 ? "Ready" : "Unavailable",
                events.Count > 0 ? $"{events.Count} configured official/manual event(s)." :
                    "No official or manually approved event calendar is configured; no event is assumed."),
        };
        var levels = Levels(historical.Length > 0 ? historical : session);
        var profile = Profile(historical.Length > 0 ? historical : session);
        var lifecycle = Lifecycle(session);
        var uncertainty = Uncertainty(volatility, optionContext, cross, capabilities,
            events, observedAtUtc);
        var meta = MetaLabel(currentDecisions.Count == 0 ? null : currentDecisions[^1], volatility, optionContext,
            cross, lifecycle, uncertainty);
        var portfolio = new PortfolioCoordinationV4(
            cross.Available && Math.Abs(cross.ReturnCorrelation) >= .75m ? "CoordinateExposure" : "Independent",
            cross.ReturnCorrelation, cross.Available && Math.Abs(cross.ReturnCorrelation) >= .75m
                ? "Nifty and Sensex are strongly correlated; simultaneous same-direction positions should share one risk budget."
                : "No strong same-direction cross-index overlap is established.");
        var size = Math.Clamp(meta.AcceptanceProbability * (1m - uncertainty.Score), .25m, 1m);
        var adaptive = new AdaptiveRiskV4(decimal.Round(size, 2), "ShadowOnly",
            ["Multiplier is research-only and cannot alter configured lots.",
             $"Meta-label {meta.AcceptanceProbability:P0}; uncertainty {uncertainty.Score:P0}."]);
        var drift = Drift(currentDecisions, baselineDecisions, volatility);
        var experiments = recommendations.Select(value => new ExperimentDefinitionV4(
            $"{value.Code}-{observedAtUtc:yyyyMMdd}", value.ProposedExperiment, "ShadowOnly",
            Math.Max(30, value.SupportingObservations * 3), 5,
            "Positive after-cost expectancy, validation win rate above champion, profit factor above 1.20, and no worse drawdown.",
            "Reject on negative validation expectancy, material drawdown deterioration, data-quality failure, or regime-specific collapse."))
            .ToArray();
        return new("advanced-market-intelligence-v4", volatility, optionContext, cross,
            capabilities, levels, profile, lifecycle, meta, uncertainty, portfolio, adaptive,
            drift, experiments);
    }

    private static VolatilityContextV4 Volatility(StrategyPriceBar[] current,
        StrategyPriceBar[] history)
    {
        var currentAtr = AtrPercent(current.TakeLast(15).ToArray());
        var windows = history.Chunk(15).Where(value => value.Length >= 10)
            .Select(AtrPercent).Where(value => value > 0m).ToArray();
        var baseline = windows.Length == 0 ? currentAtr : windows.Average();
        var percentile = windows.Length == 0 ? .5m :
            (decimal)windows.Count(value => value <= currentAtr) / windows.Length;
        var ratio = baseline <= 0m ? 1m : currentAtr / baseline;
        var state = ratio >= 1.45m ? "Expansion" : ratio <= .70m ? "Compression" :
            percentile >= .80m ? "High" : percentile <= .20m ? "Low" : "Normal";
        return new(state, currentAtr, baseline, percentile, ratio);
    }

    private static OptionContextV4 Options(IReadOnlyList<OptionResearchPointV4> source)
    {
        var valid = source.Where(value => value.LastPrice > 0m).ToArray();
        if (valid.Length == 0) return new(false, 0, 0m, 0m, 0m, 0, "Unavailable",
            "No official option-chain snapshot is available for the session.");
        var callOi = valid.Where(value => value.IsCall).Sum(value => value.OpenInterest);
        var putOi = valid.Where(value => !value.IsCall).Sum(value => value.OpenInterest);
        var callIv = valid.Where(value => value.IsCall && value.ImpliedVolatility is > 0m)
            .Select(value => value.ImpliedVolatility!.Value).ToArray();
        var putIv = valid.Where(value => !value.IsCall && value.ImpliedVolatility is > 0m)
            .Select(value => value.ImpliedVolatility!.Value).ToArray();
        var allIv = callIv.Concat(putIv).ToArray();
        var liquid = valid.Count(value => value.OpenInterest > 0m && value.Volume > 0m);
        return new(true, valid.Length, callOi <= 0m ? 0m : putOi / callOi,
            Average(allIv), Average(putIv) - Average(callIv), liquid,
            "LiveOnly", $"Official chain contains {valid.Length} contracts; {liquid} have both OI and volume.");
    }

    private static CrossMarketContextV4 CrossMarket(StrategyPriceBar[] primary,
        IReadOnlyList<StrategyPriceBar> peer)
    {
        var other = peer.OrderBy(value => value.OpenTimeUtc).ToArray();
        var length = Math.Min(primary.Length, other.Length);
        if (length < 10) return new(false, 0m, "Unavailable",
            "At least ten aligned peer-index bars are required.");
        var first = primary.TakeLast(length).Select(value => value.Close).ToArray();
        var second = other.TakeLast(length).Select(value => value.Close).ToArray();
        var a = Returns(first); var b = Returns(second);
        var correlation = Correlation(a, b);
        var directionA = Math.Sign(first[^1] - first[0]);
        var directionB = Math.Sign(second[^1] - second[0]);
        var alignment = directionA == directionB && directionA != 0 ? "Confirmed" : "Divergent";
        return new(true, correlation, alignment,
            $"Peer-index return correlation is {correlation:F2}; session direction is {alignment.ToLowerInvariant()}.");
    }

    private static PriceLevelV4[] Levels(StrategyPriceBar[] source)
    {
        if (source.Length == 0) return [];
        var high = source.Max(value => value.High); var low = source.Min(value => value.Low);
        var latest = source[^1].Close;
        var roundStep = latest >= 50_000m ? 500m : latest >= 10_000m ? 100m : 50m;
        var round = Math.Round(latest / roundStep, MidpointRounding.AwayFromZero) * roundStep;
        var pivots = source.Skip(2).SkipLast(2).Select((bar, index) => new
            {
                Bar = bar, Window = source.Skip(index).Take(5).ToArray()
            }).Where(value => value.Bar.High >= value.Window.Max(x => x.High) ||
                              value.Bar.Low <= value.Window.Min(x => x.Low)).ToArray();
        var result = new List<PriceLevelV4>
        {
            new("Rolling high", high, 1m, "Highest stored price in the research window."),
            new("Rolling low", low, 1m, "Lowest stored price in the research window."),
            new("Round number", round, .55m, $"Nearest {roundStep:F0}-point psychological level.")
        };
        result.AddRange(pivots.TakeLast(5).Select(value => new PriceLevelV4("Repeated pivot",
            value.Bar.Close, .65m, "Local five-bar rejection pivot.")));
        return result.OrderBy(value => Math.Abs(value.Price - latest)).Take(8).ToArray();
    }

    private static VolumeProfileV4 Profile(StrategyPriceBar[] source)
    {
        var withVolume = source.Where(value => value.Volume > 0m).ToArray();
        if (withVolume.Length < 10) return new(false, null, null, null, null,
            "Underlying volume is unavailable; a synthetic profile is not created.");
        var low = withVolume.Min(value => value.Low); var high = withVolume.Max(value => value.High);
        var width = (high - low) / 20m;
        if (width <= 0m) return new(false, null, null, null, null, "Price range has no usable width.");
        var bins = withVolume.GroupBy(value => Math.Clamp((int)((value.Close - low) / width), 0, 19))
            .Select(group => new { Index = group.Key, Volume = group.Sum(value => value.Volume) })
            .OrderByDescending(value => value.Volume).ToArray();
        var total = bins.Sum(value => value.Volume); var accumulated = 0m;
        var valueBins = bins.TakeWhile(value => { accumulated += value.Volume; return accumulated <= total * .70m || accumulated == value.Volume; }).ToArray();
        decimal Price(int index) => low + width * (index + .5m);
        var totalVolume = withVolume.Sum(value => value.Volume);
        var anchoredVwap = totalVolume <= 0m ? (decimal?)null : withVolume.Sum(value =>
            ((value.High + value.Low + value.Close) / 3m) * value.Volume) / totalVolume;
        return new(true, Price(bins[0].Index), Price(valueBins.Min(value => value.Index)),
            Price(valueBins.Max(value => value.Index)), anchoredVwap,
            "Seventy-percent close-volume value area and VWAP anchored to the research-window start.");
    }

    private static GenericSetupLifecycleV4 Lifecycle(StrategyPriceBar[] source)
    {
        if (source.Length < 15) return new("Unavailable", null, 0m, 0,
            "At least fifteen bars are required.");
        var fast = Ema(source.Select(value => value.Close).ToArray(), 9);
        var slow = Ema(source.Select(value => value.Close).ToArray(), Math.Min(21, source.Length));
        var direction = fast > slow ? Direction.Buy : fast < slow ? Direction.Sell : (Direction?)null;
        var atr = Atr(source.TakeLast(15).ToArray());
        if (direction is null || atr <= 0m) return new("Invalidated", null, 0m, 0,
            "Direction or ATR is unavailable.");
        var prior = source.SkipLast(1).TakeLast(8).ToArray(); var latest = source[^1];
        var trigger = direction == Direction.Buy ? prior.Max(value => value.High) : prior.Min(value => value.Low);
        var extension = (direction == Direction.Buy ? latest.Close - trigger : trigger - latest.Close) / atr;
        var age = source.Reverse().TakeWhile(value => direction == Direction.Buy ? value.Close >= slow : value.Close <= slow).Count();
        var phase = extension > .65m ? "Extended" : extension >= 0m ? "Triggered" :
            extension >= -.30m ? "Ready" : "Forming";
        return new(phase, direction, extension, age,
            $"{direction} setup is {extension:F2} ATR beyond its eight-bar trigger and {age} bars old.");
    }

    private static MetaLabelV4 MetaLabel(DecisionFeatureSnapshotV2? decision,
        VolatilityContextV4 volatility, OptionContextV4 options,
        CrossMarketContextV4 cross, GenericSetupLifecycleV4 lifecycle,
        UncertaintyV4 uncertainty)
    {
        if (decision is null) return new(0m, "NoCandidate", ["No candidate decision is available."]);
        var probability = decision.CandidateConfidence;
        probability += lifecycle.Phase is "Ready" or "Triggered" ? .08m : lifecycle.Phase == "Extended" ? -.18m : 0m;
        probability += cross.Alignment == "Confirmed" ? .06m : cross.Alignment == "Divergent" ? -.06m : 0m;
        probability += options.Available && options.LiquidContracts >= 4 ? .04m : -.04m;
        probability += volatility.State == "Expansion" ? .03m : volatility.State == "Compression" ? -.04m : 0m;
        probability -= uncertainty.Score * .20m;
        probability = Math.Clamp(probability, .01m, .99m);
        return new(probability, probability >= .70m ? "AcceptCandidate" :
            probability >= .55m ? "ReviewCandidate" : "Abstain",
            [$"Base confidence {decision.CandidateConfidence:P0}.",
             $"Lifecycle {lifecycle.Phase}; cross-market {cross.Alignment}; volatility {volatility.State}.",
             $"Uncertainty penalty {uncertainty.Score:P0}."]);
    }

    private static UncertaintyV4 Uncertainty(VolatilityContextV4 volatility,
        OptionContextV4 options, CrossMarketContextV4 cross,
        IReadOnlyList<CapabilityStatusV4> capabilities,
        IReadOnlyList<ScheduledMarketEventV4> events, DateTimeOffset now)
    {
        var reasons = new List<string>(); var score = 0m;
        if (!options.Available) { score += .20m; reasons.Add("Option context unavailable."); }
        if (!cross.Available) { score += .15m; reasons.Add("Peer-index confirmation unavailable."); }
        if (volatility.BaselineAtrPercent <= 0m) { score += .15m; reasons.Add("Volatility baseline unavailable."); }
        if (capabilities.Any(value => value.Capability == "Constituent breadth" && value.Status == "Unavailable"))
        { score += .10m; reasons.Add("Licensed breadth feed unavailable."); }
        if (events.Any(value => Math.Abs((value.StartsAtUtc - now).TotalMinutes) <= value.RiskWindowMinutes))
        { score += .25m; reasons.Add("Scheduled event risk window active."); }
        score = Math.Clamp(score, 0m, 1m);
        return new(score, score >= .45m ? "Abstain" : score >= .25m ? "Caution" : "Normal",
            reasons.Count == 0 ? ["All configured context sources are available."] : reasons);
    }

    private static DriftDetectionV4 Drift(IReadOnlyList<DecisionFeatureSnapshotV2> current,
        IReadOnlyList<DecisionFeatureSnapshotV2> baseline, VolatilityContextV4 volatility)
    {
        decimal TrendRate(IReadOnlyList<DecisionFeatureSnapshotV2> values) => values.Count == 0 ? 0m :
            (decimal)values.Count(value => value.State.State is IntradayMarketStateV2.BullTrend or IntradayMarketStateV2.BearTrend) / values.Count;
        var shift = Math.Abs(TrendRate(current) - TrendRate(baseline));
        var status = baseline.Count < 30 ? "CollectBaseline" :
            volatility.ExpansionRatio is > 1.75m or < .55m || shift >= .35m ? "DriftAlert" : "Stable";
        return new(status, volatility.ExpansionRatio, shift,
            [$"Volatility ratio versus baseline is {volatility.ExpansionRatio:F2}.",
             $"Directional-regime distribution shifted {shift:P0}.",
             $"Baseline contains {baseline.Count} decisions."]);
    }

    private static decimal AtrPercent(StrategyPriceBar[] source)
    {
        var atr = Atr(source); var close = source.LastOrDefault()?.Close ?? 0m;
        return close <= 0m ? 0m : atr / close * 100m;
    }
    private static decimal Atr(StrategyPriceBar[] source) => source.Length < 2 ? 0m :
        source.Skip(1).Select((bar, index) => Math.Max(bar.High - bar.Low,
            Math.Max(Math.Abs(bar.High - source[index].Close), Math.Abs(bar.Low - source[index].Close)))).Average();
    private static decimal Ema(decimal[] source, int period)
    { var k = 2m / (period + 1m); var result = source[0]; foreach (var value in source.Skip(1)) result = value * k + result * (1m - k); return result; }
    private static decimal[] Returns(decimal[] source) => source.Zip(source.Skip(1),
        (left, right) => left == 0m ? 0m : (right - left) / left).ToArray();
    private static decimal Correlation(decimal[] a, decimal[] b)
    {
        var count = Math.Min(a.Length, b.Length); if (count < 2) return 0m;
        var x = a.Take(count).ToArray(); var y = b.Take(count).ToArray();
        var ax = x.Average(); var ay = y.Average();
        var numerator = x.Zip(y, (left, right) => (left - ax) * (right - ay)).Sum();
        var denominator = (decimal)Math.Sqrt((double)(x.Sum(value => (value - ax) * (value - ax)) * y.Sum(value => (value - ay) * (value - ay))));
        return denominator == 0m ? 0m : Math.Clamp(numerator / denominator, -1m, 1m);
    }
    private static decimal Average(IEnumerable<decimal> values)
    { var array = values.ToArray(); return array.Length == 0 ? 0m : array.Average(); }
}
