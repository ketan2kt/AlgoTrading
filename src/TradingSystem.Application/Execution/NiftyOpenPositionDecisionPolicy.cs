using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Execution;

// Pure Nifty paper-exit ordering shared by the live paper loop and historical
// replay. Evidence acquisition (quotes, completed bars, manual request) stays
// outside this policy; absent evidence must be passed as false.
public static class NiftyOpenPositionDecisionPolicy
{
    public static PaperExitReason ResolveExit(Direction direction,
        decimal currentPrice, decimal effectiveStop, decimal target,
        TimeOnly currentIndiaTime, TimeOnly forcedExitTime,
        bool optionQuoteFresh, bool extendTarget,
        bool manualExitRequested, bool killSwitchActive,
        bool confirmedUnderlyingInvalidation,
        bool confirmedFailedSetup, bool carryApproved)
    {
        var reason = PaperPositionExitPolicy.Evaluate(direction, currentPrice,
            effectiveStop, target, currentIndiaTime, forcedExitTime,
            optionQuoteFresh, targetExitEnabled: !extendTarget);
        if (manualExitRequested) return PaperExitReason.ManualExit;
        if (killSwitchActive && optionQuoteFresh)
            return PaperExitReason.EmergencyKillSwitch;
        if (reason == PaperExitReason.ForcedIntradayExit && carryApproved)
            return PaperExitReason.None;
        if (reason != PaperExitReason.None) return reason;
        if (confirmedUnderlyingInvalidation)
            return PaperExitReason.UnderlyingTrendInvalidated;
        return confirmedFailedSetup ? PaperExitReason.FailedSetup :
            PaperExitReason.None;
    }

    public static int PartialQuantity(Direction direction,
        decimal currentPrice, decimal entryPrice, decimal initialStop,
        int totalQuantity, ProfessionalExitPlan exitPlan,
        bool partialProfitEnabled, bool alreadyPartiallyExited,
        PaperExitReason resolvedExit)
    {
        if (resolvedExit != PaperExitReason.None || !partialProfitEnabled ||
            alreadyPartiallyExited || totalQuantity <= 1) return 0;
        var risk = Math.Abs(entryPrice - initialStop);
        if (risk <= 0m) return 0;
        var reached = direction == Direction.Buy
            ? currentPrice >= entryPrice + risk * exitPlan.PartialProfitR
            : currentPrice <= entryPrice - risk * exitPlan.PartialProfitR;
        return reached ? Math.Clamp(
            (int)Math.Floor(totalQuantity * exitPlan.PartialExitFraction),
            1, Math.Max(1, totalQuantity - 1)) : 0;
    }
}
