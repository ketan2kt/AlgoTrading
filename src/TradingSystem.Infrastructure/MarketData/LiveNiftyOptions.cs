namespace TradingSystem.Infrastructure.MarketData;

public sealed class LiveNiftyOptions
{
    public const string SectionName = "MarketData:LiveNifty";

    public bool Enabled { get; init; }
    public string Exchange { get; init; } = "NSE";
    public string Segment { get; init; } = "CASH";
    public string TradingSymbol { get; init; } = "NIFTY";
    public int PollIntervalSeconds { get; init; } = 2;
    public int WorkspaceCandleCount { get; init; } = 1500;
}
