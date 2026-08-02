using TradingSystem.Domain.Trading;

namespace TradingSystem.Infrastructure.Broker;

internal sealed class PaperBrokerStateStore
{
    internal SemaphoreSlim Gate { get; } = new(1, 1);
    internal Dictionary<string, PaperOrderState> Orders { get; } =
        new(StringComparer.Ordinal);
    internal Dictionary<(Guid InstrumentId, Direction Direction), PaperPositionState> Positions { get; } = [];
    internal bool IsRestored { get; set; }
    internal long NextEventSequence { get; set; }
    internal long NextOrderSequence { get; set; }
}

internal sealed class PaperOrderState
{
    public required string ClientReference { get; init; }
    public required string BrokerOrderId { get; init; }
    public required Guid InstrumentId { get; init; }
    public required Direction Direction { get; init; }
    public required int RequestedQuantity { get; init; }
    public required decimal ExecutionPrice { get; init; }
    public required int MaximumFillQuantityPerCycle { get; init; }
    public required DateTimeOffset UpdatedAtUtc { get; set; }
    public int FilledQuantity { get; set; }
    public decimal? AverageFillPrice { get; set; }
    public OrderState State { get; set; }
}

internal sealed class PaperPositionState
{
    public required Guid InstrumentId { get; init; }
    public required Direction Direction { get; init; }
    public int Quantity { get; set; }
    public decimal AverageEntryPrice { get; set; }
}
