namespace TradingSystem.Application.Strategies;

public static class StrategyEvaluationAuditCadence
{
    public static bool IsNoTradeAuditDue(DateTimeOffset sessionStartUtc,
        DateTimeOffset candleTimeUtc, DateTimeOffset? lastAuditUtc, int intervalMinutes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(intervalMinutes);
        var baseline = lastAuditUtc ?? sessionStartUtc;
        return candleTimeUtc >= baseline.AddMinutes(intervalMinutes);
    }
}
