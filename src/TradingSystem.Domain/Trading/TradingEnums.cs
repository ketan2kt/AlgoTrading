namespace TradingSystem.Domain.Trading;

public enum InstrumentSegment
{
    Cash = 0,
    FuturesAndOptions = 1
}

public enum InstrumentType
{
    Equity = 0,
    Index = 1,
    Future = 2,
    CallOption = 3,
    PutOption = 4
}

public enum Direction
{
    Buy = 0,
    Sell = 1
}

public enum SignalStatus
{
    Generated = 0,
    Accepted = 1,
    Rejected = 2,
    Expired = 3
}

public enum OrderState
{
    Created = 0,
    ValidationPending = 1,
    RejectedByRisk = 2,
    ReadyToSubmit = 3,
    Submitted = 4,
    BrokerAcknowledged = 5,
    PartiallyFilled = 6,
    Filled = 7,
    CancelPending = 8,
    Cancelled = 9,
    RejectedByBroker = 10,
    ExitPending = 11,
    Closed = 12,
    ReconciliationRequired = 13,
    Failed = 14
}

public enum PositionState
{
    Pending = 0,
    Open = 1,
    ExitPending = 2,
    Closed = 3,
    ReconciliationRequired = 4
}

public enum MarketRegime
{
    Uncertain = 0,
    StrongBullishTrend = 1,
    WeakBullishTrend = 2,
    StrongBearishTrend = 3,
    WeakBearishTrend = 4,
    RangeBound = 5,
    HighVolatilityExpansion = 6,
    LowVolatilityCompression = 7,
    GapUpContinuation = 8,
    GapUpRejection = 9,
    GapDownContinuation = 10,
    GapDownReversal = 11
}

public enum SessionState
{
    Planned = 0,
    Preparing = 1,
    TradingSuspended = 2,
    TradingEnabled = 3,
    Closing = 4,
    Reconciled = 5,
    Failed = 6
}
