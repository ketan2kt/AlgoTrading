namespace TradingSystem.Application.Execution;

public sealed record PersistentDirectionalStopDecision(
    bool ShouldExit,
    bool Deferred,
    IReadOnlyList<string> Reasons);

/// <summary>
/// Prevents an explicitly paper-only directional thesis from being invalidated by a
/// transient option-premium print. Profit-protection stops remain immediate; this policy
/// applies only to the original loss stop and always retains a catastrophic loss boundary.
/// </summary>
public static class PersistentDirectionalStopPolicy
{
    public static readonly TimeSpan MinimumObservation = TimeSpan.FromMinutes(10);
    public const decimal CatastrophicAdverseRiskMultiple = 2m;

    public static PersistentDirectionalStopDecision Evaluate(
        bool persistentDirectionalPaperTrade,
        bool initialLossStopHit,
        TimeSpan elapsed,
        decimal adverseRiskMultiple,
        bool underlyingThesisInvalidated)
    {
        if (!initialLossStopHit)
            return new(false, false, ["The initial loss stop has not been reached."]);
        if (!persistentDirectionalPaperTrade)
            return new(true, false, ["The normal premium stop remains authoritative."]);
        if (underlyingThesisInvalidated)
            return new(true, false,
                ["The option stop and a confirmed underlying change of character agree."]);
        if (adverseRiskMultiple >= CatastrophicAdverseRiskMultiple)
            return new(true, false,
                [$"The catastrophic paper boundary of {CatastrophicAdverseRiskMultiple:F1}R was reached."]);

        var observation = elapsed < MinimumObservation
            ? $"Only {Math.Max(0, elapsed.TotalMinutes):F1} minutes have elapsed; wait for two completed five-minute candles."
            : "The underlying directional thesis remains valid after the observation window.";
        return new(false, true,
            [observation, "The premium-only stop is deferred while the paper thesis remains valid."]);
    }
}

public sealed record DirectionFlipDecision(bool Permitted, IReadOnlyList<string> Reasons);

/// <summary>
/// A rapid opposite-side re-entry is usually the same opening whipsaw, not a fresh setup.
/// Same-side pullbacks are unaffected; an opposite side receives a full structural-reset window.
/// </summary>
public static class DirectionFlipCooldownPolicy
{
    public static readonly TimeSpan MinimumResetWindow = TimeSpan.FromMinutes(30);

    public static DirectionFlipDecision Evaluate(DateTimeOffset? lastOppositeExitAtUtc,
        DateTimeOffset nowUtc)
    {
        if (lastOppositeExitAtUtc is null)
            return new(true, ["No opposite-side exit requires a reset."]);
        var elapsed = nowUtc - lastOppositeExitAtUtc.Value;
        if (elapsed >= MinimumResetWindow)
            return new(true, ["The opposite-side structural-reset window has completed."]);
        var remaining = MinimumResetWindow - elapsed;
        return new(false,
            [$"Opposite-side whipsaw protection remains active for {Math.Ceiling(remaining.TotalMinutes)} minute(s)."]);
    }
}
