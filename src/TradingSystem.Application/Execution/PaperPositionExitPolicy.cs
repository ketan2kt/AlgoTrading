using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Execution;

public enum PaperExitReason
{
    None = 0,
    StopLoss = 1,
    Target = 2,
    ForcedIntradayExit = 3,
    EmergencyKillSwitch = 4,
    UnderlyingTrendInvalidated = 5
}

public sealed class PaperPositionExitPolicy
{
    public static PaperExitReason Evaluate(Direction direction, decimal currentPrice, decimal stopLoss,
        decimal target, TimeOnly currentIndiaTime, TimeOnly forcedExitTime, bool marketDataFresh,
        bool targetExitEnabled = true)
    {
        if (currentPrice <= 0 || stopLoss <= 0 || target <= 0)
            throw new ArgumentOutOfRangeException(nameof(currentPrice), "Prices must be positive.");
        if (!marketDataFresh) return PaperExitReason.None;

        var stopHit = direction == Direction.Buy ? currentPrice <= stopLoss : currentPrice >= stopLoss;
        if (stopHit) return PaperExitReason.StopLoss;
        var targetHit = direction == Direction.Buy ? currentPrice >= target : currentPrice <= target;
        if (targetHit && targetExitEnabled) return PaperExitReason.Target;
        return currentIndiaTime >= forcedExitTime
            ? PaperExitReason.ForcedIntradayExit
            : PaperExitReason.None;
    }
}
