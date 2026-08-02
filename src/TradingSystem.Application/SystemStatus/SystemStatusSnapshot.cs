using TradingSystem.Domain;

namespace TradingSystem.Application.SystemStatus;

public sealed record SystemStatusSnapshot(
    TradingMode Mode,
    bool LiveTradingAvailable,
    bool TradingEnabled,
    string Status,
    DateTimeOffset ObservedAtUtc);

