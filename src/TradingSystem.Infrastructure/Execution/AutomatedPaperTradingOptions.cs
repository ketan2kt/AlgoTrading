namespace TradingSystem.Infrastructure.Execution;

public sealed class AutomatedPaperTradingOptions
{
    public const string SectionName = "PaperTrading:Automation";
    public bool Enabled { get; init; }
    public int EvaluationIntervalSeconds { get; init; } = 5;
    public string OpeningRangeEnd { get; init; } = "09:30";
    public string EntryCutoff { get; init; } = "14:30";
    public string ForcedExit { get; init; } = "15:15";
    public decimal MaximumDailyLoss { get; init; } = 1500m;
    public decimal MaximumEntrySlippagePercent { get; init; } = 0.10m;
    public decimal EstimatedRoundTripCostBasisPoints { get; init; } = 5m;
    public decimal MaximumOptionSpreadPercent { get; init; } = 1.5m;
    public decimal MaximumOptionPremium { get; init; } = 500m;
    public decimal MinimumOptionVolume { get; init; } = 100m;
    public decimal MinimumOptionOpenInterest { get; init; } = 100m;
    public decimal OptionStopLossPercent { get; init; } = 10m;
    public decimal OptionRewardToRiskRatio { get; init; } = 2m;
}
