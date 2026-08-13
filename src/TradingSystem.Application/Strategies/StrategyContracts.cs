using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Strategies;

public enum SignalEntryType { Market = 0, Limit = 1, Stop = 2 }

public sealed record StrategySignal(
    Guid SignalId, string StrategyId, string StrategyVersion, Guid InstrumentId,
    Direction Direction, SignalEntryType EntryType, decimal ProposedEntry,
    decimal ProposedStopLoss, decimal ProposedTarget, decimal RewardToRiskRatio,
    decimal Confidence, MarketRegime Regime, IReadOnlyList<string> SupportingReasons,
    IReadOnlyList<string> InvalidatingReasons, DateTimeOffset MarketDataTimestampUtc,
    DateTimeOffset ExpiresAtUtc);

public sealed record StrategyEvaluationContext(
    Guid InstrumentId, DateTimeOffset ObservedAtUtc, decimal CurrentPrice,
    decimal OpeningRangeHigh, decimal OpeningRangeLow, decimal RelativeVolume,
    MarketRegime Regime, Direction? RegimeBias, decimal RegimeConfidence,
    bool RegimeTradingPermitted, bool DataTradingPermitted,
    DateTimeOffset? LastSignalAtUtc, int TradesToday)
{
    public decimal Vwap { get; init; }
    public decimal FastEma { get; init; }
    public decimal SlowEma { get; init; }
    public decimal AtrPercent { get; init; }
    public IReadOnlyList<StrategyPriceBar> RecentCandles { get; init; } = [];
    public MarketStructureSnapshot MarketStructure { get; init; } = MarketStructureSnapshot.Unavailable;
    public IReadOnlyDictionary<string, int> TradesByStrategyToday { get; init; } =
        new Dictionary<string, int>(StringComparer.Ordinal);
}

public sealed record StrategyPriceBar(
    DateTimeOffset OpenTimeUtc, decimal Open, decimal High, decimal Low, decimal Close);

public enum MarketStructureDirection { Unavailable = 0, Bullish = 1, Bearish = 2, Range = 3 }

public sealed record MarketStructureSnapshot(
    MarketStructureDirection Direction,
    decimal Strength,
    decimal RecentSwingHigh,
    decimal RecentSwingLow,
    int ConsecutiveDirectionalSwings)
{
    public static MarketStructureSnapshot Unavailable { get; } =
        new(MarketStructureDirection.Unavailable, 0m, 0m, 0m, 0);
}

public sealed record StrategyEvaluationResult(
    StrategySignal? Signal,
    IReadOnlyList<string> FailedConditions);

public interface ITradingStrategy
{
    string StrategyId { get; }
    string Version { get; }
    StrategySignal? Evaluate(StrategyEvaluationContext context);
    StrategyEvaluationResult EvaluateDetailed(StrategyEvaluationContext context);
}
