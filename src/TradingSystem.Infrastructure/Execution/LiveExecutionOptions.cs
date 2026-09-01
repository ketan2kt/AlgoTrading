namespace TradingSystem.Infrastructure.Execution;

public sealed class LiveExecutionOptions
{
    public const string SectionName = "LiveExecution";
    public bool BuildEnabled { get; init; }
    public int MaximumLotsPerOrder { get; init; } = 5;
    public int ControlledTestLotsPerOrder { get; init; } = 1;
    public string[] AllowedMarkets { get; init; } = ["NIFTY", "SENSEX"];
}
