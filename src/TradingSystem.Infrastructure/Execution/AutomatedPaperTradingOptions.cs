namespace TradingSystem.Infrastructure.Execution;

public sealed class AutomatedPaperTradingOptions
{
    public const string SectionName = "PaperTrading:Automation";
    public bool Enabled { get; init; }
    public int EvaluationIntervalSeconds { get; init; } = 5;
    public int NoTradeAuditIntervalMinutes { get; init; } = 15;
    public string EntryWindowStart { get; init; } = "09:15";
    public string OpeningRangeEnd { get; init; } = "09:30";
    public string EntryCutoff { get; init; } = "14:30";
    public string ForcedExit { get; init; } = "15:15";
    public decimal MaximumDailyLoss { get; init; } = 1500m;
    public decimal MaximumEntrySlippagePercent { get; init; } = 0.10m;
    public decimal MaximumOptionSpreadPercent { get; init; } = 1.5m;
    public decimal MaximumOptionPremium { get; init; } = 500m;
    public decimal MinimumOptionVolume { get; init; } = 100m;
    public decimal MinimumOptionOpenInterest { get; init; } = 100m;
    public decimal OptionStopLossPercent { get; init; } = 10m;
    public decimal OptionRewardToRiskRatio { get; init; } = 1m;
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
    public decimal ProfitLockTriggerRiskMultiple { get; init; } = 0.90m;
    public decimal ProfitLockRiskMultiple { get; init; } = 0.80m;
    public bool PartialProfitEnabled { get; init; } = true;
    public decimal PartialProfitRiskMultiple { get; init; } = 1m;
    public decimal PartialExitFraction { get; init; } = 0.40m;
    public bool UnderlyingTrendInvalidationEnabled { get; init; } = true;
    public decimal MinimumReversalStructureStrength { get; init; } = 0.55m;
    public int RequiredReversalEvidenceCount { get; init; } = 3;
    public bool ProtectProfitableConfirmedReversals { get; init; } = true;
    public decimal ReversalProfitLockFraction { get; init; } = 0.80m;
    public int ExitResearchMaximumDelayMinutes { get; init; } = 3;
    public int MinimumSignalCandles { get; init; } = 4;
    public int MinimumFuturesConfirmationCandles { get; init; } = 2;
    public int PositionRecoveryLookbackDays { get; init; } = 14;
    public int MaximumConcurrentPositions { get; init; } = 8;
    public bool SelectiveHedgingEnabled { get; init; } = true;
    public decimal HedgeMinimumAtrPercent { get; init; } = 0.35m;
    public decimal HedgeMinimumRelativeFuturesVolume { get; init; } = 1m;
    public decimal HedgeMinimumSignalConfidence { get; init; } = 0.60m;
    public bool WeeklyCarryForwardEnabled { get; init; }
    public int WeeklyCarryMaximumDaysToExpiry { get; init; } = 7;
    public decimal WeeklyCarryMinimumConfidence { get; init; } = 0.65m;
}
