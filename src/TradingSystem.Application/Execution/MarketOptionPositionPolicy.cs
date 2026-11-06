using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Execution;

// Existing normal-session Sensex/Bank Nifty decisions, without DB, clock or
// broker calls. Evidence acquisition and order placement remain outside.
public sealed record MarketOptionPositionDecision(string? ExitReason, int PartialQuantity);

public static class MarketOptionPositionPolicy
{
    // These two established trend-entry paths do not contain "directional" or
    // "continuation" in their names. Do not let naming disable their runners.
    // Keep their existing stop/partial plan unchanged; live structure must still qualify.
    public static bool AllowsTargetExtension(string strategy) =>
        ProfessionalExitPolicy.For(Regime(strategy)).ExtendTarget ||
        strategy.Equals("Index momentum breakout", StringComparison.OrdinalIgnoreCase) ||
        strategy.Equals("Sensex controlled early pullback", StringComparison.OrdinalIgnoreCase);

    public static ProfessionalTradeRegime Regime(string strategy) =>
        strategy.Contains("range", StringComparison.OrdinalIgnoreCase)
            ? ProfessionalTradeRegime.Range
            : strategy.Contains("directional", StringComparison.OrdinalIgnoreCase) ||
              strategy.Contains("continuation", StringComparison.OrdinalIgnoreCase)
                ? ProfessionalTradeRegime.Directional : ProfessionalTradeRegime.Transition;

    public static decimal ProtectedStop(Direction direction, decimal entry,
        decimal initialRisk, decimal favourablePrice, decimal existingStop,
        int remainingQuantity, ProfessionalExitPlan plan, string exchangeCode = "NSE",
        bool differentiatedRunner = false)
    {
        var initialStop = direction == Direction.Buy ? entry - initialRisk : entry + initialRisk;
        var stop = PaperProtectiveStopPolicy.Calculate(direction, entry, initialStop,
            favourablePrice, plan.BreakEvenTriggerR, plan.TrailDistanceR,
            plan.ProfitLockTriggerR, plan.ProfitLockR, differentiatedRunner);
        var gain = direction == Direction.Buy ? favourablePrice - entry : entry - favourablePrice;
        if (gain >= initialRisk * plan.BreakEvenTriggerR)
        {
            var costs = PaperTradingCostModel.CalculateOptionCharges([
                new(PaperOptionTransactionSide.Buy, entry, remainingQuantity),
                new(PaperOptionTransactionSide.Sell, Math.Max(.05m, stop), remainingQuantity)], exchangeCode).Total;
            stop = PaperProtectiveStopPolicy.ProtectNetProfit(direction, entry, stop,
                remainingQuantity, costs);
        }
        return direction == Direction.Buy ? Math.Max(existingStop, stop) : Math.Min(existingStop, stop);
    }

    public static (bool ShouldExit, bool ProtectProfit, decimal Stop) Reversal(
        string market, UnderlyingTrendInvalidationResult invalidation,
        Direction optionDirection, decimal entry, decimal current, decimal existingStop,
        bool protectProfitable, decimal lockFraction, decimal tickSize)
    {
        if (market is not ("sensex" or "bank-nifty") || !invalidation.ShouldExit)
            return (false, false, existingStop);
        var favourable = optionDirection == Direction.Buy ? current - entry : entry - current;
        if (favourable <= 0m || !protectProfitable) return (true, false, existingStop);
        var protectedStop = PaperProtectiveStopPolicy.ProtectReversalProfit(optionDirection,
            entry, current, existingStop, lockFraction);
        var rounded = optionDirection == Direction.Buy
            ? Math.Floor(protectedStop / tickSize) * tickSize
            : Math.Ceiling(protectedStop / tickSize) * tickSize;
        return (false, true, optionDirection == Direction.Buy
            ? Math.Max(existingStop, rounded) : Math.Min(existingStop, rounded));
    }

    public static bool Continuation(IReadOnlyList<StrategyPriceBar> completedBars,
        Direction direction)
    {
        var bars = completedBars.TakeLast(21).ToArray();
        if (bars.Length < 9) return false;
        var fast = Ema(bars, 9);
        var slow = Ema(bars, Math.Min(21, bars.Length));
        return direction == Direction.Buy ? fast > slow && bars[^1].Close >= fast
            : fast < slow && bars[^1].Close <= fast;
    }

    public static bool FreshContinuation(IReadOnlyList<StrategyPriceBar> bars,
        Direction direction, DateTimeOffset observedAt, DateTimeOffset? underlyingAt,
        int intervalSeconds, int maximumAgeSeconds)
    {
        if (underlyingAt is null || underlyingAt > observedAt ||
            observedAt - underlyingAt.Value > TimeSpan.FromSeconds(maximumAgeSeconds)) return false;
        var indiaOffset = TimeSpan.FromHours(5.5);
        var day = observedAt.ToOffset(indiaOffset).Date;
        var completed = bars.Where(bar => bar.OpenTimeUtc.ToOffset(indiaOffset).Date == day &&
                bar.OpenTimeUtc.AddSeconds(intervalSeconds) <= observedAt)
            .OrderBy(bar => bar.OpenTimeUtc).TakeLast(21).ToArray();
        if (completed.Length < 9 || observedAt - completed[^1].OpenTimeUtc.AddSeconds(intervalSeconds) >
            TimeSpan.FromSeconds(intervalSeconds + maximumAgeSeconds) || !Continuation(completed, direction))
            return false;
        return UnderlyingTrendInvalidationPolicy.Evaluate(direction, completed,
            Ema(completed, 9), Ema(completed, Math.Min(21, completed.Length))).Behaviour == SessionBehaviour.Trending;
    }

    public static UnderlyingTrendInvalidationResult Invalidation(
        IReadOnlyList<StrategyPriceBar> completedSessionBars, Direction direction,
        DateTimeOffset openedAtUtc, DateTimeOffset observedAtUtc, int intervalSeconds,
        int maximumUnderlyingAgeSeconds, decimal minimumStrength, int requiredEvidence)
    {
        var bars = completedSessionBars.Where(value => value.OpenTimeUtc.AddSeconds(intervalSeconds)
                <= observedAtUtc).TakeLast(30).ToArray();
        if (bars.Length < 21)
            return new(false, 0m, ["At least 21 completed underlying candles are required."]);
        if (bars.Count(value => value.OpenTimeUtc.AddSeconds(intervalSeconds) > openedAtUtc) < 2)
            return new(false, 0m, ["Waiting for two completed underlying candles after entry."]);
        if (observedAtUtc - bars[^1].OpenTimeUtc.AddSeconds(intervalSeconds) >
            TimeSpan.FromSeconds(intervalSeconds + maximumUnderlyingAgeSeconds))
            return new(false, 0m, ["Completed underlying candles are stale."]);
        return UnderlyingTrendInvalidationPolicy.Evaluate(direction, bars, Ema(bars, 9),
            Ema(bars, Math.Min(21, bars.Length)), minimumStrength, requiredEvidence);
    }

    public static MarketOptionPositionDecision Decide(Direction direction,
        decimal price, decimal entry, decimal stop, decimal target, decimal initialRisk,
        int remainingQuantity, int lotSize, ProfessionalExitPlan plan,
        bool manual, bool reversalExit, bool failedSetup, bool continuation,
        bool forced, bool partialEnabled, bool alreadyPartial)
    {
        var stopHit = direction == Direction.Buy ? price <= stop : price >= stop;
        var targetHit = direction == Direction.Buy ? price >= target : price <= target;
        var reason = manual ? "ManualExit" : stopHit ? "StopLossHit" :
            reversalExit ? "UnderlyingTrendInvalidated" : failedSetup ? "FailedSetupExit" :
            targetHit && !continuation ? "TargetHit" : forced ? "TimeExit" : null;
        // Preserve the production reason priority if target and time coincide.
        if (forced && targetHit && reason == "TimeExit") reason = "TargetHit";
        if (reason is not null || !partialEnabled || alreadyPartial || lotSize <= 0 ||
            remainingQuantity <= lotSize) return new(reason, 0);
        var threshold = direction == Direction.Buy ? entry + initialRisk * plan.PartialProfitR
            : entry - initialRisk * plan.PartialProfitR;
        var reached = direction == Direction.Buy ? price >= threshold : price <= threshold;
        if (!reached) return new(null, 0);
        var lots = Math.Max(1, remainingQuantity / lotSize);
        var partialLots = Math.Clamp((int)Math.Floor(lots * plan.PartialExitFraction),
            1, Math.Max(1, lots - 1));
        return new(null, partialLots * lotSize);
    }

    private static decimal Ema(StrategyPriceBar[] bars, int period)
    {
        var result = bars[0].Close;
        var multiplier = 2m / (period + 1m);
        foreach (var bar in bars.Skip(1)) result += (bar.Close - result) * multiplier;
        return result;
    }
}
