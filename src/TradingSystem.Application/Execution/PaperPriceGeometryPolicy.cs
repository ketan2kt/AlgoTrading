using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Execution;

public static class PaperPriceGeometryPolicy
{
    public static bool IsValid(Direction direction, decimal entry, decimal stop, decimal target) =>
        entry > 0 && stop > 0 && target > 0 &&
        (direction == Direction.Buy
            ? stop < entry && target > entry
            : stop > entry && target < entry);
}
