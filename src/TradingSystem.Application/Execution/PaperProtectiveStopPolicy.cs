using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Execution;

public static class PaperProtectiveStopPolicy
{
    public static decimal Calculate(Direction direction, decimal entry, decimal initialStop,
        decimal highWater, decimal breakEvenTriggerR, decimal trailingDistanceR)
    {
        var risk = Math.Abs(entry - initialStop);
        if (risk <= 0) throw new ArgumentOutOfRangeException(nameof(initialStop));
        if (direction == Direction.Buy)
        {
            var result = initialStop;
            if (highWater >= entry + risk * breakEvenTriggerR) result = Math.Max(result, entry);
            if (highWater >= entry + risk * (breakEvenTriggerR + trailingDistanceR))
                result = Math.Max(result, highWater - risk * trailingDistanceR);
            return result;
        }
        var resultForShort = initialStop;
        if (highWater <= entry - risk * breakEvenTriggerR) resultForShort = Math.Min(resultForShort, entry);
        if (highWater <= entry - risk * (breakEvenTriggerR + trailingDistanceR))
            resultForShort = Math.Min(resultForShort, highWater + risk * trailingDistanceR);
        return resultForShort;
    }
}
