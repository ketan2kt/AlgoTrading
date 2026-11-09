namespace TradingSystem.Application.Execution;

public sealed record HeroZeroBasketProtectionState(decimal EntryDebit, decimal PeakNetPnl,
    decimal ProfitFloor, bool ProfitActivated, string? ExitReason);

// Basket-only protection: never add separate-leg peaks or discard the red leg.
public static class HeroZeroBasketProtectionPolicy
{
    public static HeroZeroBasketProtectionState Evaluate(decimal entryDebit, decimal? netLiquidationPnl,
        bool executableBook, HeroZeroBasketProtectionState? previous, decimal lossPercent,
        decimal activationPercent, decimal givebackFraction, bool forcedExit, bool legRiskBreached = false)
    {
        if (entryDebit <= 0m || lossPercent is <= 0m or > 50m || activationPercent <= 0m ||
            givebackFraction is <= 0m or >= .5m) throw new ArgumentOutOfRangeException(nameof(entryDebit));
        var state = previous ?? new(entryDebit, 0m, -entryDebit * lossPercent / 100m, false, null);
        if (state.EntryDebit != entryDebit || state.PeakNetPnl < 0m ||
            state.ProfitActivated && (state.ProfitFloor < 0m || state.ProfitFloor > state.PeakNetPnl))
            return state with { ExitReason = "HeroZeroProtectionStateInvalid" };
        if (state.ExitReason is not null) return state;
        if (forcedExit) return state with { ExitReason = "HeroZeroTimeExit" };
        if (legRiskBreached) return state with { ExitReason = "HeroZeroLegRiskExit" };
        if (netLiquidationPnl is not { } current) return state;
        // LTP may protect downside when the book is unavailable, but cannot
        // establish or raise an allegedly executable profit watermark.
        var peak = executableBook ? Math.Max(state.PeakNetPnl, current) : state.PeakNetPnl;
        var activated = state.ProfitActivated || peak >= entryDebit * activationPercent / 100m;
        var floor = activated ? Math.Max(state.ProfitFloor, peak * (1m - givebackFraction))
            : Math.Max(state.ProfitFloor, -entryDebit * lossPercent / 100m);
        var reason = current <= floor
            ? activated ? "HeroZeroBasketProfitProtection" : "HeroZeroBasketStop" : null;
        return state with { PeakNetPnl = peak, ProfitFloor = floor, ProfitActivated = activated, ExitReason = reason };
    }
}
