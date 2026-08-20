using TradingSystem.Domain.Common;

namespace TradingSystem.Domain.Trading;

public sealed class MarketPaperPosition : MutableEntity
{
    public MarketPaperPosition(Guid id, string market, Guid underlyingInstrumentId,
        Guid executionInstrumentId, string strategy, Direction direction, int quantity,
        decimal entryPrice, decimal stopLoss, decimal target, DateTimeOffset openedAtUtc)
        : base(id, openedAtUtc)
    {
        Market = market; UnderlyingInstrumentId = underlyingInstrumentId;
        ExecutionInstrumentId = executionInstrumentId; Strategy = strategy; Direction = direction;
        Quantity = quantity; EntryPrice = entryPrice; CurrentPrice = entryPrice;
        StopLoss = stopLoss; Target = target; OpenedAtUtc = openedAtUtc.ToUniversalTime();
    }

    public string Market { get; private init; }
    public Guid UnderlyingInstrumentId { get; private init; }
    public Guid ExecutionInstrumentId { get; private init; }
    public string Strategy { get; private init; }
    public Direction Direction { get; private init; }
    public int Quantity { get; private init; }
    public decimal EntryPrice { get; private init; }
    public decimal CurrentPrice { get; private set; }
    public decimal StopLoss { get; private set; }
    public decimal Target { get; private init; }
    public decimal RealisedPnl { get; private set; }
    public string Status { get; private set; } = "Active";
    public DateTimeOffset OpenedAtUtc { get; private init; }
    public DateTimeOffset? ClosedAtUtc { get; private set; }

    public void Mark(decimal price, decimal trailingStop)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(price);
        CurrentPrice = price;
        StopLoss = Direction == Direction.Buy ? Math.Max(StopLoss, trailingStop) : Math.Min(StopLoss, trailingStop);
    }

    public void Close(decimal price, decimal costs, string status, DateTimeOffset closedAtUtc)
    {
        if (Status != "Active") throw new InvalidOperationException("Position is already closed.");
        CurrentPrice = price;
        RealisedPnl = (Direction == Direction.Buy ? price - EntryPrice : EntryPrice - price) * Quantity - costs;
        Status = status;
        ClosedAtUtc = closedAtUtc.ToUniversalTime();
    }
}

public sealed class MarketStrategyAudit : Entity, IAppendOnlyEntity
{
    public MarketStrategyAudit(Guid id, string market, Guid underlyingInstrumentId,
        DateTimeOffset candleTimeUtc, string outcome, decimal confidence, string reasonsJson)
        : base(id)
    {
        Market = market; UnderlyingInstrumentId = underlyingInstrumentId;
        CandleTimeUtc = candleTimeUtc.ToUniversalTime(); Outcome = outcome;
        Confidence = confidence; ReasonsJson = reasonsJson;
    }
    public string Market { get; private init; }
    public Guid UnderlyingInstrumentId { get; private init; }
    public DateTimeOffset CandleTimeUtc { get; private init; }
    public string Outcome { get; private init; }
    public decimal Confidence { get; private init; }
    public string ReasonsJson { get; private init; }
}
