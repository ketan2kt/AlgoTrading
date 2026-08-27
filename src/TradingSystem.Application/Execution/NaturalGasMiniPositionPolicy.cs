using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Execution;

public static class NaturalGasMiniPositionPolicy
{
    public const int ExpectedLotSize = 250;
    public const int FixedLots = 4;
    public const int FixedQuantity = ExpectedLotSize * FixedLots;
    public const int MaximumEntriesPerSession = 3;
    public const int ReentryCooldownMinutes = 90;
    public const decimal MinimumStopAtrMultiple = 1.5m;
    public const decimal TargetRiskMultiple = 2m;
    public const decimal TrailActivationRiskMultiple = 1.5m;
    public const decimal TrailProfitLockRiskMultiple = 0.5m;

    public static bool IsSupportedContract(int lotSize) => lotSize == ExpectedLotSize;

    public static decimal WidenStop(Direction direction, decimal entry, decimal structuralStop,
        decimal atr) => direction == Direction.Buy
        ? Math.Min(structuralStop, entry - atr * MinimumStopAtrMultiple)
        : Math.Max(structuralStop, entry + atr * MinimumStopAtrMultiple);

    public static decimal ApplyTrailingStop(Direction direction, decimal entry, decimal currentStop,
        decimal currentPrice, decimal initialRisk)
    {
        var favourable = direction == Direction.Buy ? currentPrice - entry : entry - currentPrice;
        if (favourable < initialRisk * TrailActivationRiskMultiple) return currentStop;
        var lockedStop = direction == Direction.Buy
            ? entry + initialRisk * TrailProfitLockRiskMultiple
            : entry - initialRisk * TrailProfitLockRiskMultiple;
        return direction == Direction.Buy
            ? Math.Max(currentStop, lockedStop)
            : Math.Min(currentStop, lockedStop);
    }
}
