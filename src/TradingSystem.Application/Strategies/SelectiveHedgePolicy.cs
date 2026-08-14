using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Strategies;

public sealed record SelectiveHedgeInput(
    bool HasOpposingOpenExposure,
    bool HasExistingHedge,
    decimal AtrPercent,
    decimal RelativeFuturesVolume,
    decimal SignalConfidence,
    MarketRegime Regime);

public sealed record SelectiveHedgeDecision(bool Approved, IReadOnlyList<string> Reasons);

/// <summary>
/// Allows an opposite long-option leg only during validated volatility expansion. This is
/// protection for existing exposure, not a permanent long straddle/strangle rule.
/// </summary>
public static class SelectiveHedgePolicy
{
    public static SelectiveHedgeDecision Evaluate(SelectiveHedgeInput input,
        decimal minimumAtrPercent, decimal minimumRelativeVolume, decimal minimumConfidence)
    {
        var reasons = new List<string>();
        if (!input.HasOpposingOpenExposure) reasons.Add("No opposing open exposure requires protection.");
        if (input.HasExistingHedge) reasons.Add("An active hedge already protects the portfolio.");
        if (input.AtrPercent < minimumAtrPercent)
            reasons.Add($"ATR {input.AtrPercent:F2}% is below hedge threshold {minimumAtrPercent:F2}%.");
        if (input.RelativeFuturesVolume < minimumRelativeVolume)
            reasons.Add($"Relative futures volume {input.RelativeFuturesVolume:F2} is below {minimumRelativeVolume:F2}.");
        if (input.SignalConfidence < minimumConfidence)
            reasons.Add($"Opposite signal confidence {input.SignalConfidence:P0} is below {minimumConfidence:P0}.");
        if (input.Regime is MarketRegime.LowVolatilityCompression or MarketRegime.RangeBound)
            reasons.Add("The current regime does not justify paying for a protective option hedge.");
        return new(reasons.Count == 0, reasons);
    }
}

public sealed record WeeklyCarryInput(DateOnly TradingDate, DateOnly? ExpiryDate,
    Direction PositionDirection, decimal EntryPrice, decimal CurrentPrice,
    decimal SignalConfidence, bool TrendInvalidated, bool KillSwitchActive);

public sealed record WeeklyCarryDecision(bool Approved, IReadOnlyList<string> Reasons);

public static class WeeklyCarryPolicy
{
    public static WeeklyCarryDecision Evaluate(WeeklyCarryInput input,
        int maximumDaysToExpiry, decimal minimumConfidence)
    {
        var reasons = new List<string>();
        var days = input.ExpiryDate?.DayNumber - input.TradingDate.DayNumber;
        if (input.PositionDirection != Direction.Buy)
            reasons.Add("Only fully-paid long options can be carried by this paper policy.");
        if (days is null or <= 0 || days > maximumDaysToExpiry)
            reasons.Add("The option is not an eligible near-weekly contract with time remaining.");
        if (input.CurrentPrice <= input.EntryPrice)
            reasons.Add("Only a protected, profitable position may be carried overnight.");
        if (input.SignalConfidence < minimumConfidence)
            reasons.Add($"Signal confidence is below {minimumConfidence:P0}.");
        if (input.TrendInvalidated) reasons.Add("The underlying trend has been invalidated.");
        if (input.KillSwitchActive) reasons.Add("Kill switch is active.");
        return new(reasons.Count == 0, reasons);
    }
}
