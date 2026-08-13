namespace TradingSystem.Infrastructure.Execution;

public sealed class AutomatedPaperTradingOptions
{
    public const string SectionName = "PaperTrading:Automation";
    public bool Enabled { get; init; }
    public int EvaluationIntervalSeconds { get; init; } = 5;
    public int NoTradeAuditIntervalMinutes { get; init; } = 15;
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
    public int MaximumOptionLots { get; init; } = 5;
    public int MaximumOptionDaysToExpiry { get; init; } = 10;
    public int ExpiryDayInTheMoneySteps { get; init; } = 1;
    public int NormalInTheMoneySteps { get; init; }
    public decimal OptionStrikeStep { get; init; } = 50m;
    public bool PermissivePaperExecution { get; init; } = true;
    public bool BreakEvenEnabled { get; init; } = true;
    public decimal BreakEvenTriggerRiskMultiple { get; init; } = 1m;
    public bool TrailingStopEnabled { get; init; } = true;
    public decimal TrailingStopRiskMultiple { get; init; } = 1m;
    public bool PartialProfitEnabled { get; init; } = true;
    public decimal PartialProfitRiskMultiple { get; init; } = 1m;
    public decimal PartialExitFraction { get; init; } = 0.40m;
    public bool UnderlyingTrendInvalidationEnabled { get; init; } = true;
    public decimal MinimumReversalStructureStrength { get; init; } = 0.55m;
    public int RequiredReversalEvidenceCount { get; init; } = 3;
}
