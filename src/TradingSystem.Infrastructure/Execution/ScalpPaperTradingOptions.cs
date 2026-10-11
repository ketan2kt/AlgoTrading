namespace TradingSystem.Infrastructure.Execution;

public sealed class ScalpPaperTradingOptions
{
    public const string SectionName = "PaperTrading:Scalp";
    public bool Enabled { get; init; } = true;
    public int EvaluationIntervalSeconds { get; init; } = 5;
    public string EntryWindowStart { get; init; } = "09:20";
    public string EntryCutoff { get; init; } = "15:00";
    public string ForcedExit { get; init; } = "15:15";
    public int MaximumCohortsPerDay { get; init; } = 20;
    public int CooldownMinutes { get; init; } = 2;
    public decimal MinimumConfidence { get; init; } = .60m;
    public decimal StopLossPercent { get; init; } = 5m;
    public decimal QuickProfitPoints { get; init; } = 5m;
    public decimal RunnerActivationPoints { get; init; } = 4m;
    public decimal RunnerTrailingGapPoints { get; init; } = 2m;
    public decimal RunnerTargetRiskMultiple { get; init; } = 4m;
    public int MaximumHoldMinutes { get; init; } = 20;
    public int RunnerMaximumHoldMinutes { get; init; } = 45;
    public int LotsPerCohort { get; init; } = 2;
    public int MaximumDaysToExpiry { get; init; } = 10;
    public decimal MaximumDailyLoss { get; init; } = 5000m;
}
