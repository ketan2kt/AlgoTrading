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
    decimal? Target,
    string? SelectedOptionSymbol = null,
    string? SelectedOptionType = null,
    DateOnly? SelectedOptionExpiry = null,
    decimal? SelectedOptionStrike = null,
    int? SelectedOptionLotSize = null);

public interface IPaperAutomationReader
{
    PaperAutomationSnapshot GetCurrent();
}
