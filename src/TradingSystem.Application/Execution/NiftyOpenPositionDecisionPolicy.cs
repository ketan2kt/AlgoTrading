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
        int totalQuantity, int lotSize, ProfessionalExitPlan exitPlan,
        bool partialProfitEnabled, bool alreadyPartiallyExited,
        PaperExitReason resolvedExit)
    {
        if (resolvedExit != PaperExitReason.None || !partialProfitEnabled ||
            alreadyPartiallyExited || lotSize <= 0 || totalQuantity <= lotSize ||
            totalQuantity % lotSize != 0) return 0;
        var risk = Math.Abs(entryPrice - initialStop);
        if (risk <= 0m) return 0;
        var reached = direction == Direction.Buy
            ? currentPrice >= entryPrice + risk * exitPlan.PartialProfitR
            : currentPrice <= entryPrice - risk * exitPlan.PartialProfitR;
        var lots = totalQuantity / lotSize;
        return reached ? lotSize * Math.Clamp((int)Math.Floor(lots * exitPlan.PartialExitFraction),
            1, lots - 1) : 0;
    }
}
