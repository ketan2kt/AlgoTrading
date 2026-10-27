namespace TradingSystem.Infrastructure.Execution;

public sealed class LiveExecutionOptions
{
    public const string SectionName = "LiveExecution";
    public bool BuildEnabled { get; init; }
    public string PaperParityMode { get; init; } = "PaperFillAndLifecycle";
    public int PollIntervalSeconds { get; init; } = 2;
    public int SignalMaximumAgeSeconds { get; init; } = 90;
    public string[] AllowedMarkets { get; init; } = ["NIFTY", "SENSEX"];
}
