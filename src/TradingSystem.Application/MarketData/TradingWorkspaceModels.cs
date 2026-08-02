namespace TradingSystem.Application.MarketData;

public sealed record WorkspaceCandle(
    DateTimeOffset OpenTimeUtc,
    int IntervalSeconds,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    long Volume,
    bool IsClosed);

public sealed record WorkspaceTradeOverlay(
    Guid SignalId,
    string Strategy,
    string Direction,
    DateTimeOffset SignalTimeUtc,
    decimal Entry,
    decimal StopLoss,
    decimal Target,
    string Status,
    int? Quantity,
    decimal? FillPrice);

public sealed record TradingWorkspaceSnapshot(
    string Instrument,
    string Exchange,
    string Timeframe,
    string Mode,
    string FeedStatus,
    bool IsLive,
    bool IsFresh,
    DateTimeOffset? LastMarketTimestampUtc,
    DateTimeOffset ObservedAtUtc,
    string? StatusMessage,
    IReadOnlyList<WorkspaceCandle> Candles,
    IReadOnlyList<WorkspaceTradeOverlay> Overlays);

public interface ITradingWorkspaceReader
{
    Task<TradingWorkspaceSnapshot> GetNiftyAsync(int candleCount, CancellationToken cancellationToken);
}

public interface ILiveMarketDataPublisher
{
    Task PublishNiftyAsync(TradingWorkspaceSnapshot snapshot, CancellationToken cancellationToken);
}

public sealed class NullLiveMarketDataPublisher : ILiveMarketDataPublisher
{
    public Task PublishNiftyAsync(TradingWorkspaceSnapshot snapshot, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
