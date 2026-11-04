using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Execution;

// Preserve the currently deployed recovery semantics. The persisted Signal
// does not carry a regime; rebuilding it currently selects Uncertain. Replay
// must not silently use a more favourable original entry regime instead.
public static class NiftySignalRecoveryPolicy
{
    public const MarketRegime RecoveredRegime = MarketRegime.Uncertain;

    public static StrategySignal Recover(Signal value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var fingerprint = value.Fingerprint.Split(':', StringSplitOptions.RemoveEmptyEntries);
        var strategyId = fingerprint.Length >= 3 ? string.Join(':', fingerprint[..^2]) : "unknown-strategy";
        var version = fingerprint.Length >= 2 ? fingerprint[^2] : "unknown";
        return new(value.Id, strategyId, version, value.InstrumentId, value.Direction,
            SignalEntryType.Market, value.ProposedEntry, value.ProposedStopLoss, value.ProposedTarget,
            Math.Abs(value.ProposedTarget - value.ProposedEntry) /
            Math.Abs(value.ProposedEntry - value.ProposedStopLoss), value.Confidence,
            RecoveredRegime, [], [], value.MarketDataTimestampUtc, value.ExpiresAtUtc);
    }

    public static ProfessionalExitPlan ExitPlan() =>
        ProfessionalExitPolicy.For(ProfessionalExitPolicy.From(RecoveredRegime));
}
