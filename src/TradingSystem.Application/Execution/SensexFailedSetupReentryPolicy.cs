using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Execution;

public static class SensexFailedSetupReentryPolicy
{
    public static readonly TimeSpan ResetPeriod = TimeSpan.FromMinutes(15);
    public const string Rejection = "SensexFailedSetupReentryDeferred";
    private const decimal MinimumDirectionalEfficiency = .30m;

    // The caller supplies the most recent completed regular exit on this side,
    // regardless of strike. A strong trend still has to pass the normal structural reset.
    public static bool Blocks(DateTimeOffset now, DateTimeOffset? lastExit,
        string? lastStatus, decimal? lastNetPnl, MarketStateAssessmentV2? state, Direction direction)
    {
        if (lastExit is not { } closed || closed > now || now - closed >= ResetPeriod ||
            lastStatus != "FailedSetupExit" || lastNetPnl is not < 0m)
            return false;

        return state is null || state.Bias != direction ||
            ProfessionalTradeFramework.Map(state) != ProfessionalTradeRegime.Directional ||
            state.DirectionalEfficiency < MinimumDirectionalEfficiency;
    }
}
