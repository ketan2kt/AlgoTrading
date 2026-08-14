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
    int? SelectedOptionLotSize = null,
    IReadOnlyList<PaperReadinessCheck>? ReadinessChecks = null,
    decimal? CurrentOptionPrice = null,
    IReadOnlyList<PaperPositionMark>? ActivePositionMarks = null);

public sealed record PaperPositionMark(
    Guid SignalId,
    decimal? CurrentPrice,
    decimal? ExecutablePrice,
    decimal? UnrealisedPnl,
    DateTimeOffset ObservedAtUtc,
    bool QuoteAvailable);

public sealed record PaperReadinessCheck(string Code, string Label, bool Ready, string Detail);

public interface IPaperAutomationReader
{
    PaperAutomationSnapshot GetCurrent();
}
