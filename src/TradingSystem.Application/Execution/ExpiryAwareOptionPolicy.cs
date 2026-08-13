namespace TradingSystem.Application.Execution;

public static class ExpiryAwareOptionPolicy
{
    public static decimal StopLossPercent(DateOnly tradingDate, DateOnly expiryDate,
        decimal configuredPercent)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(configuredPercent);
        ArgumentOutOfRangeException.ThrowIfLessThan(expiryDate, tradingDate);
        var days = expiryDate.DayNumber - tradingDate.DayNumber;
        return days switch
        {
            0 => Math.Min(configuredPercent, 8m),
            1 => Math.Min(configuredPercent, 9m),
            _ => configuredPercent
        };
    }

    public static int MaximumLots(DateOnly tradingDate, DateOnly expiryDate, int configuredLots)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(configuredLots);
        ArgumentOutOfRangeException.ThrowIfLessThan(expiryDate, tradingDate);
        return expiryDate == tradingDate ? Math.Max(1, configuredLots / 2) : configuredLots;
    }
}
