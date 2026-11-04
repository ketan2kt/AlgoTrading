namespace TradingSystem.Application.Execution;

/// <summary>
/// A full-sized stop is stronger invalidation evidence than a near-flat trailing exit.
/// Defer only a rapid repeat of the same option side; first entries and small exits are unchanged.
/// </summary>
public static class SensexFullStopReentryPolicy
{
    public static readonly TimeSpan ResetPeriod = TimeSpan.FromMinutes(10);

    public static bool AllowsEntry(DateTimeOffset now, DateTimeOffset? priorExit,
        string? priorStatus, decimal priorNetPnl, decimal priorEntry, int priorQuantity,
        decimal initialStopPercent)
    {
        if (priorExit is null || priorStatus != "StopLossHit" || priorEntry <= 0m ||
            priorQuantity <= 0 || initialStopPercent <= 0m)
            return true;

        var initialRisk = priorEntry * initialStopPercent / 100m * priorQuantity;
        var fullStop = priorNetPnl <= -initialRisk * .75m;
        return !fullStop || now - priorExit.Value >= ResetPeriod;
    }
}
