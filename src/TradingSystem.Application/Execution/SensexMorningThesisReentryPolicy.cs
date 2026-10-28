namespace TradingSystem.Application.Execution;

/// <summary>
/// Defers only repeated persistent-thesis entries during the first 90 minutes.
/// Fresh first entries and other Sensex strategies remain eligible.
/// </summary>
public static class SensexMorningThesisReentryPolicy
{
    public static readonly TimeOnly ReentryOpensAt = new(10, 45);

    public static bool AllowsEntry(TimeOnly localTime, bool persistentThesisEntry,
        DateTimeOffset? priorSameDirectionExitUtc) =>
        !persistentThesisEntry || priorSameDirectionExitUtc is null ||
        localTime >= ReentryOpensAt;
}
