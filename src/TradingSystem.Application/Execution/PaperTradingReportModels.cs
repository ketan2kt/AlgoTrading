namespace TradingSystem.Application.Execution;

public sealed record PaperTradeHistoryItem(Guid SignalId, string Contract, string Direction,
    int Quantity, decimal EntryPrice, decimal ExitPrice, decimal RealisedPnl, string ExitReason,
    DateTimeOffset SignalTimeUtc, DateTimeOffset ExitTimeUtc, string Strategy);

public sealed record DailyPaperPerformance(DateOnly Date, int Trades, int Wins, int Losses,
    decimal NetPnl, decimal GrossProfit, decimal GrossLoss, decimal WinRate,
    decimal MaximumDrawdown);

public sealed record PaperTradingReport(IReadOnlyList<DailyPaperPerformance> Daily,
    IReadOnlyList<PaperTradeHistoryItem> Trades, DateTimeOffset ObservedAtUtc);

public interface IPaperTradingReportReader
{
    Task<PaperTradingReport> GetAsync(int days, CancellationToken cancellationToken);
}
