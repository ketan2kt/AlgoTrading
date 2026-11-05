using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;
using System.Text.Json;

namespace TradingSystem.Application.Execution;

// The entry regime is persisted in ReasonsJson by EfPaperLifecycleAuditStore.
// Restore it on every management cycle, not just on process restart. Missing
// legacy evidence remains Uncertain; never infer a favourable regime.
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
            ReadRegime(value.ReasonsJson), [], [], value.MarketDataTimestampUtc, value.ExpiresAtUtc);
    }

    public static MarketRegime ReadRegime(string? reasonsJson)
    {
        if (string.IsNullOrWhiteSpace(reasonsJson)) return RecoveredRegime;
        try
        {
            using var document = JsonDocument.Parse(reasonsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("regime", out var value)) return RecoveredRegime;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) &&
                Enum.IsDefined(typeof(MarketRegime), number)) return (MarketRegime)number;
            if (value.ValueKind == JsonValueKind.String &&
                Enum.TryParse<MarketRegime>(value.GetString(), true, out var named) &&
                Enum.IsDefined(named)) return named;
        }
        catch (JsonException) { }
        return RecoveredRegime;
    }

    public static ProfessionalExitPlan ExitPlan(MarketRegime? regime = null) =>
        ProfessionalExitPolicy.For(ProfessionalExitPolicy.From(regime ?? RecoveredRegime));
}
