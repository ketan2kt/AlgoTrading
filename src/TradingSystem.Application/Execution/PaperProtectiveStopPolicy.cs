using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Execution;

public static class PaperProtectiveStopPolicy
{
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

    // Scale protection with the size of the winning excursion, not a fixed currency
    // threshold: retain at least 50% after activation, 65% from 1.5R, and 75% from 2.5R.
    public static decimal PeakProfitLockFraction(decimal peakR) => peakR switch
    {
        >= 2.5m => .75m,
        >= 1.5m => .65m,
        _ => .50m
    };
}
