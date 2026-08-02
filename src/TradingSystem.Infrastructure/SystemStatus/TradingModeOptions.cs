using TradingSystem.Domain;

namespace TradingSystem.Infrastructure.SystemStatus;

public sealed class TradingModeOptions
{
    public const string SectionName = "Trading";

    public TradingMode Mode { get; init; } = TradingMode.Paper;
}

