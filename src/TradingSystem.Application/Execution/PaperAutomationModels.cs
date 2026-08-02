namespace TradingSystem.Application.Execution;

public sealed record PaperAutomationSnapshot(
    string Status,
    bool TradingPermitted,
    string Message,
    DateTimeOffset ObservedAtUtc,
    int TradesToday,
    decimal RealisedPnl,
    decimal UnrealisedPnl,
    Guid? ActiveSignalId,
    string? ActiveDirection,
    int? ActiveQuantity,
    decimal? EntryPrice,
    decimal? StopLoss,
    decimal? Target);

public interface IPaperAutomationReader
{
    PaperAutomationSnapshot GetCurrent();
}

