using TradingSystem.Domain.Common;

namespace TradingSystem.Domain.Trading;

public sealed class StrategyEvaluation : Entity, IAppendOnlyEntity
{
    public StrategyEvaluation(Guid id, string strategyCode, string strategyVersion,
        Guid instrumentId, DateTimeOffset candleTimeUtc, decimal currentPrice,
        decimal openingRangeHigh, decimal openingRangeLow, decimal vwap,
        decimal fastEma, decimal slowEma, decimal atrPercent, decimal relativeFuturesVolume,
        MarketRegime regime, Direction? regimeBias, decimal regimeConfidence,
        string outcome, string failedConditionsJson, Guid? signalId,
        string? optionSymbol, string? optionType, DateOnly? optionExpiry,
        decimal? optionStrike, decimal? optionPremium, DateTimeOffset recordedAtUtc) : base(id)
    {
        StrategyCode = strategyCode;
        StrategyVersion = strategyVersion;
        InstrumentId = instrumentId;
        CandleTimeUtc = candleTimeUtc.ToUniversalTime();
        CurrentPrice = currentPrice;
        OpeningRangeHigh = openingRangeHigh;
        OpeningRangeLow = openingRangeLow;
        Vwap = vwap;
        FastEma = fastEma;
        SlowEma = slowEma;
        AtrPercent = atrPercent;
        RelativeFuturesVolume = relativeFuturesVolume;
        Regime = regime;
        RegimeBias = regimeBias;
        RegimeConfidence = regimeConfidence;
        Outcome = outcome;
        FailedConditionsJson = failedConditionsJson;
        SignalId = signalId;
        OptionSymbol = optionSymbol;
        OptionType = optionType;
        OptionExpiry = optionExpiry;
        OptionStrike = optionStrike;
        OptionPremium = optionPremium;
        RecordedAtUtc = recordedAtUtc.ToUniversalTime();
    }

    public string StrategyCode { get; private init; }
    public string StrategyVersion { get; private init; }
    public Guid InstrumentId { get; private init; }
    public DateTimeOffset CandleTimeUtc { get; private init; }
    public decimal CurrentPrice { get; private init; }
    public decimal OpeningRangeHigh { get; private init; }
    public decimal OpeningRangeLow { get; private init; }
    public decimal Vwap { get; private init; }
    public decimal FastEma { get; private init; }
    public decimal SlowEma { get; private init; }
    public decimal AtrPercent { get; private init; }
    public decimal RelativeFuturesVolume { get; private init; }
    public MarketRegime Regime { get; private init; }
    public Direction? RegimeBias { get; private init; }
    public decimal RegimeConfidence { get; private init; }
    public string Outcome { get; private init; }
    public string FailedConditionsJson { get; private init; }
    public Guid? SignalId { get; private init; }
    public string? OptionSymbol { get; private init; }
    public string? OptionType { get; private init; }
    public DateOnly? OptionExpiry { get; private init; }
    public decimal? OptionStrike { get; private init; }
    public decimal? OptionPremium { get; private init; }
    public DateTimeOffset RecordedAtUtc { get; private init; }
}

public sealed class PaperTradeResult : Entity, IAppendOnlyEntity
{
    public PaperTradeResult(Guid id, Guid signalId, Guid instrumentId, string tradingSymbol,
        int quantity, decimal entryPrice, decimal exitPrice, decimal grossPnl,
        decimal estimatedCosts, decimal realisedPnl, string exitReason,
        DateTimeOffset closedAtUtc) : base(id)
    {
        SignalId = signalId;
        InstrumentId = instrumentId;
        TradingSymbol = tradingSymbol;
        Quantity = quantity;
        EntryPrice = entryPrice;
        ExitPrice = exitPrice;
        GrossPnl = grossPnl;
        EstimatedCosts = estimatedCosts;
        RealisedPnl = realisedPnl;
        ExitReason = exitReason;
        ClosedAtUtc = closedAtUtc.ToUniversalTime();
    }

    public Guid SignalId { get; private init; }
    public Guid InstrumentId { get; private init; }
    public string TradingSymbol { get; private init; }
    public int Quantity { get; private init; }
    public decimal EntryPrice { get; private init; }
    public decimal ExitPrice { get; private init; }
    public decimal GrossPnl { get; private init; }
    public decimal EstimatedCosts { get; private init; }
    public decimal RealisedPnl { get; private init; }
    public string ExitReason { get; private init; }
    public DateTimeOffset ClosedAtUtc { get; private init; }
}
