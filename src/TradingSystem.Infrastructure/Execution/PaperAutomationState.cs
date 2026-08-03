using TradingSystem.Application.Execution;

namespace TradingSystem.Infrastructure.Execution;

internal sealed class PaperAutomationState(TimeProvider timeProvider) : IPaperAutomationReader
{
    private readonly object gate = new();
    private PaperAutomationSnapshot snapshot = new(
        "Starting", false, "Paper automation is starting.", DateTimeOffset.MinValue,
        0, 0m, 0m, null, null, null, null, null, null);

    public PaperAutomationSnapshot GetCurrent()
    {
        lock (gate) return snapshot;
    }

    public void RecordReadiness(IReadOnlyList<PaperReadinessCheck> checks)
    {
        ArgumentNullException.ThrowIfNull(checks);
        lock (gate) snapshot = snapshot with { ReadinessChecks = checks };
    }

    public void Record(string status, bool permitted, string message, int tradesToday = 0,
        decimal realisedPnl = 0m, decimal unrealisedPnl = 0m, Guid? signalId = null,
        string? direction = null, int? quantity = null, decimal? entry = null,
        decimal? stop = null, decimal? target = null, string? optionSymbol = null,
        string? optionType = null, DateOnly? optionExpiry = null, decimal? optionStrike = null,
        int? optionLotSize = null)
    {
        lock (gate)
        {
            snapshot = new(status, permitted, message, timeProvider.GetUtcNow(), tradesToday,
                realisedPnl, unrealisedPnl, signalId, direction, quantity, entry, stop, target,
                optionSymbol, optionType, optionExpiry, optionStrike, optionLotSize,
                snapshot.ReadinessChecks);
        }
    }
}
