using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Strategies;

// An explicit Nifty-only entry-selectivity trial. Never used for exits or risk sizing.
public static class NiftyEntryTrialProfile
{
    public const string Version = "nifty-broader-entry-trial-v1";
    public const decimal VolumeScale = .80m;
    public const decimal ScoreReduction = .05m;
    public const decimal CooldownScale = .50m;
    public const decimal BreakoutBufferScale = .60m;
    public const decimal ExpansionRange = 1.25m;
    public const decimal SingleSignalScore = .58m;
    public const decimal MinimumOiRoom = .90m;

    public static bool TechnicalDirection(StrategyEvaluationContext context, Direction direction) =>
        context.CurrentPrice > 0m && context.Vwap > 0m &&
        context.FastEma > 0m && context.SlowEma > 0m &&
        (direction == Direction.Buy
            ? context.CurrentPrice > context.Vwap && context.FastEma > context.SlowEma
            : context.CurrentPrice < context.Vwap && context.FastEma < context.SlowEma);

    public static bool RegimeMatches(StrategyEvaluationContext context, Direction direction) =>
        context.RegimeBias == direction ||
        context.RelaxedNiftyEntries && TechnicalDirection(context, direction);
}
