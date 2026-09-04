namespace TradingSystem.Application.Execution;

public static class IndexEntryWindowPolicy
{
    public static TimeOnly LastEntryCutoff { get; } = new(15, 0);

    public static bool AllowsEntry(TimeOnly now, TimeOnly start, TimeOnly configuredCutoff) =>
        now >= start && now < configuredCutoff && now < LastEntryCutoff;
}
