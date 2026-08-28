namespace TradingSystem.Application.Execution;

public static class PaperResearchCadence
{
    public static IReadOnlyList<int> PostExitHorizonMinutes { get; } = [5, 15, 30, 60];
}
