using TradingSystem.Application.Strategies;

namespace TradingSystem.Application.Risk;

public sealed class PreliminaryRiskOptions
{
    public decimal MaximumRiskPerTrade { get; init; } = 500m;
    public int MaximumQuantity { get; init; } = 75;
    public decimal MaximumCapitalExposure { get; init; } = 200_000m;
    public decimal MinimumRewardToRiskRatio { get; init; } = 1.8m;
    public int MaximumTradesPerDay { get; init; } = 3;
    public int MaximumOpenPositions { get; init; } = 1;
}

public sealed record RiskContext(DateTimeOffset NowUtc, int TradesToday, int OpenPositions,
    decimal DailyRealisedPnl, decimal MaximumDailyLoss, bool KillSwitchActive,
    bool BrokerConnected, bool DataTradingPermitted);

public sealed record RiskDecisionResult(bool Approved, int ApprovedQuantity,
    decimal FinalStopLoss, decimal FinalTarget, IReadOnlyList<string> RejectionReasons,
    decimal RiskAmount, decimal CapitalExposure);

public sealed class PreliminaryRiskEngine(PreliminaryRiskOptions options)
{
    public RiskDecisionResult Evaluate(StrategySignal signal, RiskContext context, int quantityStep = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantityStep);
        var reasons = new List<string>();
        if (signal.ExpiresAtUtc <= context.NowUtc) reasons.Add("Signal expired.");
        if (context.KillSwitchActive) reasons.Add("Kill switch active.");
        if (!context.BrokerConnected) reasons.Add("Broker unavailable.");
        if (!context.DataTradingPermitted) reasons.Add("Market data blocks trading.");
        if (context.TradesToday >= options.MaximumTradesPerDay) reasons.Add("Daily trade limit reached.");
        if (context.OpenPositions >= options.MaximumOpenPositions) reasons.Add("Open-position limit reached.");
        if (context.DailyRealisedPnl <= -context.MaximumDailyLoss) reasons.Add("Daily loss limit reached.");
        if (signal.RewardToRiskRatio < options.MinimumRewardToRiskRatio) reasons.Add("Reward-to-risk is too low.");
        var perUnitRisk = Math.Abs(signal.ProposedEntry - signal.ProposedStopLoss);
        if (perUnitRisk <= 0) reasons.Add("Stop loss does not define positive risk.");
        var byRisk = perUnitRisk > 0 ? (int)Math.Floor(options.MaximumRiskPerTrade / perUnitRisk) : 0;
        var byCapital = signal.ProposedEntry > 0 ? (int)Math.Floor(options.MaximumCapitalExposure / signal.ProposedEntry) : 0;
        var rawQuantity = Math.Min(options.MaximumQuantity, Math.Min(byRisk, byCapital));
        var quantity = rawQuantity / quantityStep * quantityStep;
        if (quantity <= 0) reasons.Add("Conservative position sizing produced zero quantity.");
        return new(reasons.Count == 0, reasons.Count == 0 ? quantity : 0,
            signal.ProposedStopLoss, signal.ProposedTarget, reasons,
            reasons.Count == 0 ? quantity * perUnitRisk : 0,
            reasons.Count == 0 ? quantity * signal.ProposedEntry : 0);
    }
}
