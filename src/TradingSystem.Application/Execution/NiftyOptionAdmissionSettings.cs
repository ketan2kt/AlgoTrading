namespace TradingSystem.Application.Execution;

// Explicit replay configuration. It must be frozen with the strategy version;
// using application defaults silently would make comparisons irreproducible.
public sealed record NiftyOptionAdmissionSettings(
    bool PermissivePaperExecution,
    decimal MaximumSpreadPercent,
    decimal MaximumPremium,
    decimal MinimumVolume,
    decimal MinimumOpenInterest,
    decimal StopLossPercent,
    decimal RewardToRiskRatio,
    int MaximumConcurrentPositions,
    decimal MaximumRiskPerTrade,
    decimal MaximumCapitalExposure,
    decimal MaximumDailyLoss,
    TimeOnly EntryWindowStart,
    TimeOnly EntryCutoff,
    TimeOnly ForcedExitTime,
    int MaximumUnderlyingQuoteAgeSeconds,
    int MaximumOptionQuoteAgeSeconds,
    bool SelectiveHedgingEnabled,
    decimal HedgeMinimumAtrPercent,
    decimal HedgeMinimumRelativeFuturesVolume,
    decimal HedgeMinimumSignalConfidence,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    bool EnforceDailyLossLimit = false);
