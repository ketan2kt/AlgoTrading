namespace TradingSystem.Application.Execution;

public static class NaturalGasMiniPositionPolicy
{
    public const int ExpectedLotSize = 250;
    public const int FixedLots = 4;
    public const int FixedQuantity = ExpectedLotSize * FixedLots;
    public const int MaximumCallsPerCalendarMonth = 3;

    public static bool IsSupportedContract(int lotSize) => lotSize == ExpectedLotSize;
}
