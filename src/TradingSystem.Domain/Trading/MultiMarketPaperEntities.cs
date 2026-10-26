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
        Quantity = quantity; RemainingQuantity = quantity; EntryPrice = entryPrice; CurrentPrice = entryPrice;
        StopLoss = stopLoss; Target = target; OpenedAtUtc = openedAtUtc.ToUniversalTime();
        MaximumFavourablePnl = 0m;
        MaximumFavourableAtUtc = OpenedAtUtc;
        MaximumAdversePnl = 0m;
        MaximumAdverseAtUtc = OpenedAtUtc;
        ExcursionSampleCount = 1;
        ExcursionObservedFromUtc = OpenedAtUtc;
    }

    public string Market { get; private init; }
    public Guid UnderlyingInstrumentId { get; private init; }
    public Guid ExecutionInstrumentId { get; private init; }
    public string Strategy { get; private init; }
    public Direction Direction { get; private init; }
    public int Quantity { get; private init; }
    public int RemainingQuantity { get; private set; }
    public int PartialExitQuantity { get; private set; }
    public decimal? PartialExitPrice { get; private set; }
    public decimal EntryPrice { get; private init; }
    public decimal CurrentPrice { get; private set; }
    public decimal StopLoss { get; private set; }
    public decimal Target { get; private init; }
    public decimal RealisedPnl { get; private set; }
    public decimal CostsPaid { get; private set; }
    public string Status { get; private set; } = "Active";
    public DateTimeOffset OpenedAtUtc { get; private init; }
    public DateTimeOffset? ClosedAtUtc { get; private set; }
    public decimal? MaximumFavourablePnl { get; private set; }
    public DateTimeOffset? MaximumFavourableAtUtc { get; private set; }
    public decimal? MaximumAdversePnl { get; private set; }
    public DateTimeOffset? MaximumAdverseAtUtc { get; private set; }
    public int ExcursionSampleCount { get; private set; }
    public DateTimeOffset? ExcursionObservedFromUtc { get; private set; }

    public void Mark(decimal price, decimal trailingStop, DateTimeOffset observedAtUtc)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(price);
        CurrentPrice = price;
        StopLoss = Direction == Direction.Buy ? Math.Max(StopLoss, trailingStop) : Math.Min(StopLoss, trailingStop);
        RecordExcursion(CurrentPnlAt(price), observedAtUtc);
    }

    public void Close(decimal price, decimal costs, string status, DateTimeOffset closedAtUtc)
    {
        if (Status != "Active") throw new InvalidOperationException("Position is already closed.");
        CurrentPrice = price;
        RealisedPnl += (Direction == Direction.Buy ? price - EntryPrice : EntryPrice - price) *
                       RemainingQuantity - costs;
        CostsPaid += costs;
        RemainingQuantity = 0;
        Status = status;
        ClosedAtUtc = closedAtUtc.ToUniversalTime();
        RecordExcursion(RealisedPnl, closedAtUtc);
    }

    public void TakePartial(decimal price, int quantity, decimal costs, DateTimeOffset observedAtUtc)
    {
        if (Status != "Active") throw new InvalidOperationException("Position is already closed.");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(price);
        if (quantity <= 0 || quantity >= RemainingQuantity)
            throw new ArgumentOutOfRangeException(nameof(quantity));
        RealisedPnl += (Direction == Direction.Buy ? price - EntryPrice : EntryPrice - price) *
                       quantity - costs;
        CostsPaid += costs;
        RemainingQuantity -= quantity;
        PartialExitQuantity += quantity;
        PartialExitPrice = price;
        RecordExcursion(CurrentPnlAt(price), observedAtUtc);
    }

    private decimal CurrentPnlAt(decimal price) => RealisedPnl +
        (Direction == Direction.Buy ? price - EntryPrice : EntryPrice - price) * RemainingQuantity;

    private void RecordExcursion(decimal pnl, DateTimeOffset observedAtUtc)
    {
        var observed = observedAtUtc.ToUniversalTime();
        ExcursionObservedFromUtc ??= observed;
        // Existing rows have null excursion columns. Include the known entry baseline, but
        // do not imply that the pre-deployment intraday path can be reconstructed.
        if (MaximumFavourablePnl is null || MaximumFavourableAtUtc is null)
        {
            MaximumFavourablePnl = 0m;
            MaximumFavourableAtUtc = OpenedAtUtc;
        }
        if (MaximumAdversePnl is null || MaximumAdverseAtUtc is null)
        {
            MaximumAdversePnl = 0m;
            MaximumAdverseAtUtc = OpenedAtUtc;
        }
        if (MaximumFavourablePnl is null || MaximumFavourableAtUtc is null || pnl > MaximumFavourablePnl)
        {
            MaximumFavourablePnl = pnl;
            MaximumFavourableAtUtc = observed;
        }
        if (MaximumAdversePnl is null || MaximumAdverseAtUtc is null || pnl < MaximumAdversePnl)
        {
            MaximumAdversePnl = pnl;
            MaximumAdverseAtUtc = observed;
        }
        ExcursionSampleCount++;
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
