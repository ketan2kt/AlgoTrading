namespace TradingSystem.Application.MarketData;

public static class InstrumentPriceIncrement
{
    public static decimal FromGrowwInstrumentMaster(decimal value)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
        return value >= 1m ? value / 100m : value;
    }
}
