using TradingSystem.Application.Execution;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class NiftyOpenPositionDecisionPolicyTests
{
    [Fact]
    public void ProtectiveStopPrecedesReversalAndFailedSetup()
    {
        var reason = Resolve(89m, invalidation: true, failedSetup: true);
        Assert.Equal(PaperExitReason.StopLoss, reason);
    }

    [Fact]
    public void ConfirmedReversalOrFailedSetupCanExitBeforeInitialStop()
    {
        Assert.Equal(PaperExitReason.UnderlyingTrendInvalidated,
            Resolve(96m, invalidation: true));
        Assert.Equal(PaperExitReason.FailedSetup,
            Resolve(96m, failedSetup: true));
        Assert.Equal(PaperExitReason.None, Resolve(96m));
    }

    [Fact]
    public void ManualExitOverridesKillAndPriceExit()
    {
        Assert.Equal(PaperExitReason.ManualExit,
            NiftyOpenPositionDecisionPolicy.ResolveExit(Direction.Buy,
                89m, 90m, 115m, new TimeOnly(10), new TimeOnly(15, 15),
                true, false, true, true, false, false, false));
    }

    [Fact]
    public void PartialRequiresNoExitAndUsesProductionFraction()
    {
        var plan = ProfessionalExitPolicy.For(ProfessionalTradeRegime.Range);
        Assert.Equal(75, NiftyOpenPositionDecisionPolicy.PartialQuantity(
            Direction.Buy, 108m, 100m, 90m, 150, plan,
            true, false, PaperExitReason.None));
        Assert.Equal(0, NiftyOpenPositionDecisionPolicy.PartialQuantity(
            Direction.Buy, 108m, 100m, 90m, 150, plan,
            true, false, PaperExitReason.StopLoss));
    }

    private static PaperExitReason Resolve(decimal mark,
        bool invalidation = false, bool failedSetup = false) =>
        NiftyOpenPositionDecisionPolicy.ResolveExit(Direction.Buy,
            mark, 90m, 115m, new TimeOnly(10), new TimeOnly(15, 15),
            true, false, false, false, invalidation, failedSetup, false);
}
