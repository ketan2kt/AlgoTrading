using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Execution;

public enum ProfessionalSetupTier { Reject, Starter, Standard, HighConviction }
public enum ProfessionalTradeRegime { Unavailable, Directional, Range, Transition, Exhausted, Chop }

public sealed record ProfessionalTradeAssessmentInput(
    string Market,
    ProfessionalTradeRegime Regime,
    decimal Confidence,
    decimal RelativeVolume,
    decimal? RoomToRisk,
    decimal? SpreadPercent,
    long? Volume,
    long? OpenInterest,
    int DaysToExpiry,
    decimal? Delta,
    decimal? ImpliedVolatility,
    decimal AfterCostRewardToRisk,
    bool CorrelatedSameDirectionExposure,
    int ConsecutiveSameDirectionLosses,
    bool PullbackOrRetestConfirmed);

public sealed record ProfessionalTradePlan(
    bool Permitted,
    ProfessionalSetupTier Tier,
    decimal QuantityFraction,
    bool PermitWinnerAddOn,
    decimal AddOnTriggerR,
    decimal MaximumCombinedRiskMultiple,
    decimal PartialProfitR,
    decimal PartialExitFraction,
    decimal ProfitLockTriggerR,
    decimal ProfitLockR,
    decimal TrailDistanceR,
    int PreferredInTheMoneySteps,
    IReadOnlyList<string> Reasons);

/// <summary>
/// Converts the ten-point research framework into one deterministic, auditable plan.
/// Confidence is a ranking input, not a probability claim. Missing Greeks/IV are recorded
/// and handled through conservative strike/expiry proxies rather than invented values.
/// </summary>
public static class ProfessionalTradeFramework
{
    public static ProfessionalTradePlan Assess(ProfessionalTradeAssessmentInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var reasons = new List<string>();
        if (input.Regime is ProfessionalTradeRegime.Unavailable or ProfessionalTradeRegime.Chop)
            reasons.Add("No trade: the regime is unavailable or noisy chop.");
        if (input.Regime == ProfessionalTradeRegime.Exhausted)
            reasons.Add("No trade: the directional move is already mature or exhausted.");
        // Entry services already enforce valid stop/target geometry and portfolio risk.
        // Keep this estimate as research telemetry instead of a second hard veto: the
        // current option plans are close to 1R before costs, so requiring 1.15R after
        // costs would make every otherwise-valid paper entry unreachable.
        if (input.AfterCostRewardToRisk < 1.15m)
            reasons.Add($"Advisory: after-cost reward/risk {input.AfterCostRewardToRisk:F2} is below 1.15; " +
                        "paper eligibility is retained for observation.");
        if (input.SpreadPercent is > 1.50m)
            reasons.Add($"No trade: option spread {input.SpreadPercent:F2}% exceeds 1.50%.");
        if (input.Volume is > 0 and < 100)
            reasons.Add("No trade: option volume is below 100.");
        if (input.OpenInterest is > 0 and < 100)
            reasons.Add("No trade: option open interest is below 100.");
        if (input.Delta is { } delta && Math.Abs(delta) is < .35m or > .80m)
            reasons.Add($"No trade: absolute option delta {Math.Abs(delta):F2} is outside 0.35-0.80.");
        if (input.ConsecutiveSameDirectionLosses >= 2 && !input.PullbackOrRetestConfirmed)
            reasons.Add("No trade: repeated same-direction losses require a fresh pullback/retest reset.");

        if (reasons.Any(value => value.StartsWith("No trade:", StringComparison.Ordinal)))
            return Rejected(reasons);

        var tier = input.Regime == ProfessionalTradeRegime.Directional &&
                   input.Confidence >= .72m && input.RelativeVolume >= .85m &&
                   input.PullbackOrRetestConfirmed && input.RoomToRisk.GetValueOrDefault(1.5m) >= 1.25m
            ? ProfessionalSetupTier.HighConviction
            : input.Regime == ProfessionalTradeRegime.Transition || input.Confidence < .60m
                ? ProfessionalSetupTier.Starter
                : ProfessionalSetupTier.Standard;
        var quantity = tier switch
        {
            ProfessionalSetupTier.HighConviction => 1m,
            ProfessionalSetupTier.Standard => .80m,
            _ => .50m
        };
        if (input.CorrelatedSameDirectionExposure)
        {
            quantity *= .50m;
            reasons.Add("Correlated Nifty/Sensex exposure halves new risk instead of doubling it.");
        }
        if (input.DaysToExpiry == 0)
        {
            quantity *= .60m;
            reasons.Add("Expiry-day theta/gamma risk reduces regular position size.");
        }
        if (input.ImpliedVolatility is > 0.45m)
        {
            quantity *= .80m;
            reasons.Add("Elevated IV reduces size because premium sensitivity is unstable.");
        }

        var directional = input.Regime == ProfessionalTradeRegime.Directional;
        var range = input.Regime == ProfessionalTradeRegime.Range;
        reasons.Add($"{input.Regime} playbook selected; confidence is used as a tier, not a claimed win probability.");
        if (input.Delta is null) reasons.Add("Greeks unavailable; ITM distance is the conservative delta proxy.");
        if (input.ImpliedVolatility is null) reasons.Add("IV unavailable; no IV value was inferred.");
        reasons.Add("Winner add-ons require realised favourable movement plus fresh continuation structure.");

        return new(true, tier, Math.Clamp(quantity, .25m, 1m),
            directional && tier == ProfessionalSetupTier.HighConviction,
            1m, 1.25m,
            range ? .75m : directional ? 1.25m : 1m,
            range ? .50m : .33m,
            range ? .55m : .65m,
            range ? .25m : .20m,
            range ? .65m : directional ? 1.25m : .90m,
            input.DaysToExpiry <= 1 ? 1 : directional ? 1 : 0,
            reasons);
    }

    public static int ScaleQuantity(int approvedQuantity, int lotSize, decimal fraction)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(approvedQuantity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lotSize);
        if (fraction is <= 0m or > 1m) throw new ArgumentOutOfRangeException(nameof(fraction));
        var lots = Math.Max(1, approvedQuantity / lotSize);
        var scaledLots = Math.Max(1, (int)Math.Floor(lots * fraction));
        return Math.Min(approvedQuantity, scaledLots * lotSize);
    }

    public static ProfessionalTradeRegime Map(MarketStateAssessmentV2 state) => state.State switch
    {
        IntradayMarketStateV2.BullTrend or IntradayMarketStateV2.BearTrend or
            IntradayMarketStateV2.Expansion => ProfessionalTradeRegime.Directional,
        IntradayMarketStateV2.StructuredRange => ProfessionalTradeRegime.Range,
        IntradayMarketStateV2.MatureOrExhausted => ProfessionalTradeRegime.Exhausted,
        IntradayMarketStateV2.NoisyChop => ProfessionalTradeRegime.Chop,
        IntradayMarketStateV2.Transition or IntradayMarketStateV2.Compression =>
            ProfessionalTradeRegime.Transition,
        _ => ProfessionalTradeRegime.Unavailable
    };

    private static ProfessionalTradePlan Rejected(IReadOnlyList<string> reasons) =>
        new(false, ProfessionalSetupTier.Reject, 0m, false, 1m, 1m,
            1m, .33m, .65m, .20m, 1m, 1, reasons);
}

public sealed record DirectionalParticipationDecision(Direction? Direction, decimal Confidence,
    decimal StructuralStop, decimal Target, IReadOnlyList<string> Reasons);

public sealed record ProfessionalExitPlan(decimal BreakEvenTriggerR,
    decimal ProfitLockTriggerR, decimal ProfitLockR, decimal TrailDistanceR,
    decimal PartialProfitR, decimal PartialExitFraction, bool ExtendTarget);

public static class ProfessionalExitPolicy
{
    public static ProfessionalExitPlan For(ProfessionalTradeRegime regime) => regime switch
    {
        ProfessionalTradeRegime.Directional => new(.50m, .50m, .20m, 1.25m, 1.25m, .50m, true),
        ProfessionalTradeRegime.Range => new(.40m, .55m, .25m, .65m, .75m, .50m, false),
        ProfessionalTradeRegime.Transition => new(.45m, .60m, .20m, .90m, 1m, .50m, false),
        _ => new(.50m, .65m, .20m, 1m, 1m, .50m, false)
    };

    public static ProfessionalTradeRegime From(MarketRegime regime) => regime switch
    {
        MarketRegime.GapDownContinuation or MarketRegime.GapUpContinuation or
            MarketRegime.StrongBearishTrend or MarketRegime.StrongBullishTrend =>
            ProfessionalTradeRegime.Directional,
        MarketRegime.RangeBound or MarketRegime.LowVolatilityCompression => ProfessionalTradeRegime.Range,
        _ => ProfessionalTradeRegime.Transition
    };
}

public sealed record WinnerAddOnDecision(bool Permitted, decimal MaximumAdditionalRisk,
    IReadOnlyList<string> Reasons);

public static class WinnerAddOnPolicy
{
    public static WinnerAddOnDecision Evaluate(decimal favourableR, bool continuationConfirmed,
        bool addOnAlreadyExists, decimal originalRiskAmount, decimal proposedAddOnRisk,
        decimal maximumCombinedRiskMultiple = 1.25m)
    {
        var reasons = new List<string>();
        if (favourableR < 1m) reasons.Add("The original position has not reached 1R.");
        if (!continuationConfirmed) reasons.Add("Fresh continuation structure is not confirmed.");
        if (addOnAlreadyExists) reasons.Add("Only one winner add-on is permitted.");
        var maximum = Math.Max(0m, originalRiskAmount * maximumCombinedRiskMultiple);
        if (proposedAddOnRisk > maximum)
            reasons.Add("The proposed add-on exceeds the combined-risk ceiling.");
        return new(reasons.Count == 0, maximum, reasons.Count == 0
            ? ["Winner is at least 1R and fresh continuation is confirmed.",
               "One add-on is allowed; losing positions are never averaged down."]
            : reasons);
    }
}

public static class DirectionalParticipationPolicy
{
    public const string NiftyStrategy = "PaperResearch|nifty-directional-pullback";
    public const string SensexStrategy = "Sensex directional pullback participation";
    public const string Version = "1.0.0";

    public static DirectionalParticipationDecision Evaluate(IReadOnlyList<StrategyPriceBar> source,
        MarketStateAssessmentV2 state, decimal vwap, decimal fastEma, decimal slowEma)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Count < 15 || state.State is not (IntradayMarketStateV2.BullTrend or
                IntradayMarketStateV2.BearTrend or IntradayMarketStateV2.Expansion) ||
            state.Bias is null)
            return Reject("A confirmed directional regime is required.");
        var bars = source.TakeLast(15).ToArray();
        var latest = bars[^1];
        var direction = state.Bias.Value;
        var recentTen = bars.TakeLast(Math.Min(10, bars.Length)).ToArray();
        var persistentSession = recentTen.Count(value => direction == Direction.Buy
            ? value.Close > vwap : value.Close < vwap) >= 8;
        if (state.MoveMaturityAtr > 4.25m && !persistentSession)
            return Reject("The move is beyond the 4.25 ATR maturity budget without persistent VWAP-side control.");
        var aligned = direction == Direction.Buy
            ? latest.Close > vwap && fastEma > slowEma
            : latest.Close < vwap && fastEma < slowEma;
        if (!aligned) return Reject("VWAP and EMA 9/21 are not directionally aligned.");

        var atr = Atr(bars);
        if (atr <= 0m) return Reject("ATR is unavailable.");
        var recent = bars.TakeLast(5).ToArray();
        var pullbackSeen = direction == Direction.Buy
            ? recent.SkipLast(1).Any(value => value.Close < value.Open || value.Low <= fastEma + atr * .20m)
            : recent.SkipLast(1).Any(value => value.Close > value.Open || value.High >= fastEma - atr * .20m);
        var resumed = direction == Direction.Buy
            ? latest.Close > latest.Open && latest.Close > recent[^2].Close
            : latest.Close < latest.Open && latest.Close < recent[^2].Close;
        var pause = recent.SkipLast(1).TakeLast(3).ToArray();
        var pauseRange = pause.Max(value => value.High) - pause.Min(value => value.Low);
        var continuationBase = pauseRange <= atr * 1.60m && resumed &&
            (direction == Direction.Buy
                ? latest.Close > pause.Max(value => value.High)
                : latest.Close < pause.Min(value => value.Low));
        var extensionFromFast = Math.Abs(latest.Close - fastEma) / atr;
        if ((!pullbackSeen || !resumed) && !continuationBase)
            return Reject("Waiting for either a completed pullback resumption or a compact continuation-base break.");
        var maximumExtension = continuationBase ? 1.75m : 1.25m;
        if (extensionFromFast > maximumExtension)
            return Reject($"Continuation is {extensionFromFast:F2} ATR from EMA 9 and would be a chase.");

        var swing = direction == Direction.Buy
            ? recent.Min(value => value.Low) : recent.Max(value => value.High);
        var stop = direction == Direction.Buy
            ? Math.Min(swing, latest.Close - atr * .70m)
            : Math.Max(swing, latest.Close + atr * .70m);
        var risk = Math.Abs(latest.Close - stop);
        var target = direction == Direction.Buy
            ? latest.Close + risk * 1.8m : latest.Close - risk * 1.8m;
        var confidence = Math.Clamp(.55m + state.DirectionalEfficiency * .25m +
                                    Math.Min(.12m, state.EmaSeparationAtr * .10m), .55m, .82m);
        return new(direction, confidence, stop, target,
            ["Directional regime is aligned with VWAP and EMA 9/21.",
             continuationBase
                 ? "A compact continuation base broke in the persistent direction."
                 : "A completed pullback/retest was followed by a resumption candle.",
             $"Move maturity is {state.MoveMaturityAtr:F2} ATR; entry is not a raw breakout chase."]);
    }

    private static DirectionalParticipationDecision Reject(string reason) =>
        new(null, 0m, 0m, 0m, [reason]);

    private static decimal Atr(StrategyPriceBar[] bars) => bars.Skip(1)
        .Select((bar, index) => Math.Max(bar.High - bar.Low,
            Math.Max(Math.Abs(bar.High - bars[index].Close), Math.Abs(bar.Low - bars[index].Close))))
        .DefaultIfEmpty(0m).Average();
}
