using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Execution;

public static class PaperProtectiveStopPolicy
{
    public static decimal ExecutableTrail(Direction direction, decimal proposed, decimal prior, decimal tick)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tick);
        return direction == Direction.Buy ? Math.Max(Math.Ceiling(prior / tick), Math.Floor(proposed / tick)) * tick :
            Math.Min(Math.Floor(prior / tick), Math.Ceiling(proposed / tick)) * tick;
    }
    // Shared premium protection calculation for production and offline replay.
    // The caller supplies the favourable mark observed as-of this decision;
    // neither path may substitute a later high or a non-executable peak.
    public static decimal CalculateNetProtected(Direction direction, decimal entry,
        decimal initialStop, decimal favourable, ProfessionalExitPlan plan,
        bool breakEvenEnabled, bool trailingEnabled, int costQuantity,
        int protectionQuantity, string exchangeCode)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(costQuantity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(protectionQuantity);
        var stop = initialStop;
        if (breakEvenEnabled || trailingEnabled)
            stop = Calculate(direction, entry, initialStop, favourable,
                breakEvenEnabled ? plan.BreakEvenTriggerR : decimal.MaxValue,
                trailingEnabled ? plan.TrailDistanceR : decimal.MaxValue,
                plan.ProfitLockTriggerR, plan.ProfitLockR);
        var risk = Math.Abs(entry - initialStop);
        if (risk <= 0m) throw new ArgumentOutOfRangeException(nameof(initialStop));
        var favourableMove = direction == Direction.Buy ? favourable - entry :
            entry - favourable;
        if (favourableMove < risk * plan.BreakEvenTriggerR) return stop;
        var charges = PaperTradingCostModel.CalculateOptionCharges([
            new(direction == Direction.Buy ? PaperOptionTransactionSide.Buy :
                PaperOptionTransactionSide.Sell, entry, costQuantity),
            new(direction == Direction.Buy ? PaperOptionTransactionSide.Sell :
                PaperOptionTransactionSide.Buy, Math.Max(.05m, stop),
                costQuantity)
        ], exchangeCode).Total;
        return ProtectNetProfit(direction, entry, stop, protectionQuantity,
            charges);
    }

    public static decimal ProtectNetProfit(Direction direction, decimal entry,
        decimal existingStop, int quantity, decimal estimatedCosts, decimal tickSize = .05m)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);
        if (estimatedCosts < 0m || tickSize <= 0m)
            throw new ArgumentOutOfRangeException(nameof(estimatedCosts));
        var costDistance = decimal.Ceiling((estimatedCosts / quantity + tickSize) / tickSize) * tickSize;
        var protectedStop = direction == Direction.Buy
            ? entry + costDistance : entry - costDistance;
        return direction == Direction.Buy
            ? Math.Max(existingStop, protectedStop)
            : Math.Min(existingStop, protectedStop);
    }

    public static decimal ProtectReversalProfit(Direction direction, decimal entry,
        decimal currentPrice, decimal existingStop, decimal lockFraction)
    {
        if (lockFraction is <= 0 or >= 1) throw new ArgumentOutOfRangeException(nameof(lockFraction));
        var favourableMove = direction == Direction.Buy ? currentPrice - entry : entry - currentPrice;
        if (favourableMove <= 0) return existingStop;
        var protectedStop = direction == Direction.Buy
            ? entry + favourableMove * lockFraction
            : entry - favourableMove * lockFraction;
        return direction == Direction.Buy
            ? Math.Max(existingStop, protectedStop)
            : Math.Min(existingStop, protectedStop);
    }

    public static decimal Calculate(Direction direction, decimal entry, decimal initialStop,
        decimal highWater, decimal breakEvenTriggerR, decimal trailingDistanceR,
        decimal profitLockTriggerR = decimal.MaxValue, decimal profitLockR = 0m)
    {
        var risk = Math.Abs(entry - initialStop);
        if (risk <= 0) throw new ArgumentOutOfRangeException(nameof(initialStop));
        if (profitLockR < 0 || profitLockR >= profitLockTriggerR)
            throw new ArgumentOutOfRangeException(nameof(profitLockR),
                "Profit lock must be non-negative and below its trigger.");
        if (direction == Direction.Buy)
        {
            var result = initialStop;
            if (highWater >= entry + risk * breakEvenTriggerR) result = Math.Max(result, entry);
            if (profitLockTriggerR != decimal.MaxValue &&
                highWater >= entry + risk * profitLockTriggerR)
                result = Math.Max(result, entry + Math.Max(risk * profitLockR,
                    (highWater - entry) * PeakProfitLockFraction((highWater - entry) / risk)));
            if (highWater >= entry + risk * (breakEvenTriggerR + trailingDistanceR))
                result = Math.Max(result, highWater - risk * trailingDistanceR);
            return result;
        }
        var resultForShort = initialStop;
        if (highWater <= entry - risk * breakEvenTriggerR) resultForShort = Math.Min(resultForShort, entry);
        if (profitLockTriggerR != decimal.MaxValue &&
            highWater <= entry - risk * profitLockTriggerR)
            resultForShort = Math.Min(resultForShort, entry - Math.Max(risk * profitLockR,
                (entry - highWater) * PeakProfitLockFraction((entry - highWater) / risk)));
        if (highWater <= entry - risk * (breakEvenTriggerR + trailingDistanceR))
            resultForShort = Math.Min(resultForShort, highWater + risk * trailingDistanceR);
        return resultForShort;
    }

    // Protect near-1R winners before a confirmed reversal while preserving room for
    // early pullbacks. The same premium-based policy applies to both indices.
    public static decimal PeakProfitLockFraction(decimal peakR) => peakR switch
    {
        >= 2.5m => .75m,
        >= 1.5m => .70m,
        >= .90m => .65m,
        _ => .50m
    };
}
