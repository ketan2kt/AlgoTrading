namespace TradingSystem.Application.Execution;

public sealed record PersistentDirectionalStopDecision(
    bool ShouldExit,
    bool Deferred,
    IReadOnlyList<string> Reasons);

/// <summary>
/// A breached premium stop is authoritative, including for persistent paper theses.
/// Directional conviction must never defer the position's declared loss boundary.
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
        return new(true, false,
            ["The premium stop is authoritative; directional conviction and elapsed time cannot defer it."]);
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
