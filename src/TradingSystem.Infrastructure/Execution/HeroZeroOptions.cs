namespace TradingSystem.Infrastructure.Execution;

public sealed class HeroZeroOptions
{
    public const string SectionName = "PaperTrading:HeroZero";
    public bool Enabled { get; init; } = true;
    public int ScanIntervalSeconds { get; init; } = 30;
    public string EntryWindowStart { get; init; } = "13:30";
    public string EntryCutoff { get; init; } = "15:00";
    public string ForcedExit { get; init; } = "15:20";
    public decimal TargetPremium { get; init; } = 20m;
    public decimal MinimumPremium { get; init; } = 5m;
    public decimal MaximumPremium { get; init; } = 40m;
    public decimal MaximumSpreadPercent { get; init; } = 15m;
    public decimal MinimumCandidateScore { get; init; } = 0.55m;
    public decimal MaximumCombinedPremium { get; init; } = 70m;
    public decimal CombinedStopLossPercent { get; init; } = 35m;
    public decimal WinnerActivationMultiple { get; init; } = 1.75m;
    public decimal WinnerTrailingFraction { get; init; } = 0.25m;
    public int NearbyContractsPerSide { get; init; } = 8;
}
