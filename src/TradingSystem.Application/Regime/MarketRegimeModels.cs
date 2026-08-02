using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Regime;

public sealed record MarketRegimeInput(
    Guid TradingSessionId,
    DateTimeOffset ObservedAtUtc,
    decimal CurrentPrice,
    decimal PreviousClose,
    decimal OpeningPrice,
    decimal OpeningRangeHigh,
    decimal OpeningRangeLow,
    decimal Vwap,
    decimal FastEma,
    decimal SlowEma,
    decimal AtrPercent,
    decimal RelativeVolume,
    decimal DataQuality,
    bool MarketDataTradingPermitted);

public sealed record MarketRegimeResult(
    MarketRegime Regime,
    Direction? DirectionalBias,
    decimal Confidence,
    IReadOnlyList<string> SupportingFactors,
    IReadOnlyList<string> ContradictingFactors,
    decimal DataQuality,
    bool TradingPermitted,
    DateTimeOffset ObservedAtUtc);

public sealed class MarketRegimeOptions
{
    public const string SectionName = "MarketRegime";
    public decimal MinimumDataQuality { get; init; } = 0.90m;
    public decimal MinimumTradingConfidence { get; init; } = 0.65m;
    public decimal GapThresholdPercent { get; init; } = 0.40m;
    public decimal StrongTrendEmaSpreadPercent { get; init; } = 0.25m;
    public decimal WeakTrendEmaSpreadPercent { get; init; } = 0.08m;
    public decimal HighAtrPercent { get; init; } = 0.80m;
    public decimal LowAtrPercent { get; init; } = 0.25m;
    public decimal ExpansionRelativeVolume { get; init; } = 1.50m;
}

public interface IMarketRegimePersistence
{
    Task PersistAsync(Guid tradingSessionId, MarketRegimeResult result,
        CancellationToken cancellationToken);
}

public interface IMarketRegimeReader
{
    MarketRegimeResult? GetLatest();
}
