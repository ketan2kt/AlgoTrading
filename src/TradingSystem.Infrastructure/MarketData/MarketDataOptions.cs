namespace TradingSystem.Infrastructure.MarketData;

public sealed class MarketDataOptions
{
    public const string SectionName = "MarketData";
    public int MaximumAgeSeconds { get; init; } = 5;
    public int CandleIntervalSeconds { get; init; } = 60;
}
