namespace TradingSystem.Application.Risk;

public sealed record PaperPortfolioRiskInput(
    decimal EntryPrice,
    decimal StopLoss,
    int LotSize,
    int MaximumLots,
    decimal MaximumRiskPerTrade,
    decimal MaximumPortfolioExposure,
    decimal ExistingCapitalExposure,
    decimal DailyRealisedPnl,
    decimal DailyUnrealisedPnl,
    decimal MaximumDailyLoss,
    int OpenPositions,
    int MaximumOpenPositions,
    bool KillSwitchActive,
    bool BrokerReconciled,
    bool MarketDataFresh,
    bool DailyLossLimitOverridden = false);

public static class PaperPortfolioRiskPolicy
{
    public static RiskDecisionResult Evaluate(PaperPortfolioRiskInput input)
    {
        var reasons = new List<string>();
        if (input.EntryPrice <= 0) reasons.Add("Entry price must be positive.");
        if (input.LotSize <= 0 || input.MaximumLots <= 0) reasons.Add("A positive lot size and lot limit are required.");
        if (input.KillSwitchActive) reasons.Add("Kill switch active.");
        if (!input.BrokerReconciled) reasons.Add("Broker reconciliation is required.");
        if (!input.MarketDataFresh) reasons.Add("Market data is stale.");
        if (input.OpenPositions >= input.MaximumOpenPositions) reasons.Add("Open-position limit reached.");
        if (!input.DailyLossLimitOverridden &&
            input.DailyRealisedPnl + Math.Min(0m, input.DailyUnrealisedPnl) <= -input.MaximumDailyLoss)
            reasons.Add("Daily loss limit reached, including open losses.");

        var perUnitRisk = Math.Abs(input.EntryPrice - input.StopLoss);
        if (perUnitRisk <= 0) reasons.Add("Stop loss does not define positive risk.");
        var exposureRemaining = Math.Max(0m, input.MaximumPortfolioExposure - input.ExistingCapitalExposure);
        var maximumQuantity = checked(input.LotSize * Math.Max(0, input.MaximumLots));
        var byRisk = perUnitRisk > 0
            ? (int)Math.Min(int.MaxValue, Math.Floor(input.MaximumRiskPerTrade / perUnitRisk))
            : 0;
        var byExposure = input.EntryPrice > 0
            ? (int)Math.Min(int.MaxValue, Math.Floor(exposureRemaining / input.EntryPrice))
            : 0;
        var rawQuantity = Math.Min(maximumQuantity, Math.Min(byRisk, byExposure));
        var quantity = input.LotSize > 0 ? rawQuantity / input.LotSize * input.LotSize : 0;
        if (quantity <= 0) reasons.Add("Risk or portfolio exposure remaining is below one complete lot.");

        return new(reasons.Count == 0, reasons.Count == 0 ? quantity : 0,
            input.StopLoss, input.EntryPrice + perUnitRisk, reasons,
            reasons.Count == 0 ? quantity * perUnitRisk : 0m,
            reasons.Count == 0 ? quantity * input.EntryPrice : 0m);
    }
}
