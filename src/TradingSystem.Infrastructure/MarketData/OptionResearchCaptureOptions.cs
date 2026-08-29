namespace TradingSystem.Infrastructure.MarketData;

public sealed class OptionResearchCaptureOptions
{
    public const string SectionName = "MarketData:OptionResearchCapture";

    public bool Enabled { get; init; }
    public int IntervalSeconds { get; init; } = 60;
    public int StrikesEachSide { get; init; } = 12;
    public string SessionStart { get; init; } = "09:15";
    public string SessionEnd { get; init; } = "15:30";
}
