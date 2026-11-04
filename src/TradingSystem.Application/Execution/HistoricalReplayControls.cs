using System.Collections.Frozen;

namespace TradingSystem.Application.Execution;

public enum HistoricalReplayControlKind { KillSwitch, ReconciliationHold, DailyLossOverride, ManualExit }

// Exogenous commands, not original strategy decisions. Receipt time is the
// first instant a replay can observe them. A manual exit refers to positions
// already open at that time, never a later same-contract re-entry.
public sealed record HistoricalReplayControlEvent(Guid Id, DateTimeOffset AvailableAtUtc,
    string Market, HistoricalReplayControlKind Kind, bool Enabled, string? Symbol = null);

public sealed record HistoricalReplayControlState(bool NiftyKillSwitch,
    bool NiftyReconciliationHold, bool NiftyDailyLossOverride,
    IReadOnlyDictionary<string, DateTimeOffset> ManualExitRequests)
{
    public static HistoricalReplayControlState Empty { get; } = new(false, false, false,
        FrozenDictionary<string, DateTimeOffset>.Empty);
    public bool BlocksNiftyEntries => NiftyKillSwitch || NiftyReconciliationHold;
    public bool ManualExitRequested(HistoricalReplayOpenPosition position) =>
        ManualExitRequests.TryGetValue(position.Symbol, out var at) && at >= position.EntryAtUtc;
}

public static class HistoricalReplayControls
{
    public static HistoricalReplayControlEvent[] Validate(IEnumerable<HistoricalReplayControlEvent> source)
    {
        var events = source.Distinct().ToArray();
        if (events.Any(value => value is null || value.Id == Guid.Empty || value.AvailableAtUtc == default ||
                !Enum.IsDefined(value.Kind) || value.Market is not ("nifty" or "sensex" or "bank-nifty") ||
                value.Kind != HistoricalReplayControlKind.ManualExit && value.Market != "nifty" ||
                value.Kind == HistoricalReplayControlKind.ManualExit && (!value.Enabled ||
                    string.IsNullOrWhiteSpace(value.Symbol) || !value.Symbol.StartsWith(SymbolPrefix(value.Market), StringComparison.Ordinal))) ||
            events.GroupBy(value => value.Id).Any(group => group.Count() > 1) ||
            events.GroupBy(value => (value.AvailableAtUtc, value.Market, value.Kind, value.Symbol))
                .Any(group => group.Select(value => value.Enabled).Distinct().Count() > 1))
            throw new ArgumentException("Conflicting or unsupported historical control events.", nameof(source));
        return events.OrderBy(value => value.AvailableAtUtc).ThenBy(value => value.Id).ToArray();
    }

    public static HistoricalReplayControlState At(IReadOnlyList<HistoricalReplayControlEvent> events, DateTimeOffset at)
    {
        var visible = events.Where(value => value.AvailableAtUtc <= at)
            .OrderBy(value => value.AvailableAtUtc).ThenBy(value => value.Id).ToArray();
        bool Enabled(HistoricalReplayControlKind kind) => visible.LastOrDefault(value => value.Kind == kind)?.Enabled ?? false;
        var manual = visible.Where(value => value.Kind == HistoricalReplayControlKind.ManualExit)
            .GroupBy(value => value.Symbol!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Max(value => value.AvailableAtUtc), StringComparer.Ordinal);
        return new(Enabled(HistoricalReplayControlKind.KillSwitch), Enabled(HistoricalReplayControlKind.ReconciliationHold),
            Enabled(HistoricalReplayControlKind.DailyLossOverride), manual);
    }

    private static string SymbolPrefix(string market) => market switch
        { "nifty" => "NIFTY", "sensex" => "SENSEX", "bank-nifty" => "BANKNIFTY", _ => string.Empty };
}

// Model the controls actually used by the current production paths. Nifty's
// kill/reconciliation gates are NOT invented for Sensex or Bank Nifty. Manual
// exits are shared by all three. Their fills still require a later fresh quote.
public sealed class HistoricalNormalControlPolicy(IHistoricalReplayPortfolioPolicy policy,
    string market, int maximumQuoteAgeSeconds) : IHistoricalReplayPortfolioPolicy
{
    public string CodeVersion => policy.CodeVersion + "/normal-controls-v1";
    public string ConfigurationManifest => policy.ConfigurationManifest + "|controls=" + market + ":" + maximumQuoteAgeSeconds;
    public bool ProductionPolicyParityVerified => policy.ProductionPolicyParityVerified;
    private bool Blocked(HistoricalReplayPortfolioFrame frame) => market == "NIFTY" && frame.Controls.BlocksNiftyEntries;
    public IReadOnlyList<HistoricalReplayCandidate> Evaluate(HistoricalReplayPortfolioFrame frame) => Blocked(frame) ? [] : policy.Evaluate(frame);
    public HistoricalReplayCandidate? AssessDeferredEntry(HistoricalReplayPortfolioFrame frame,
        HistoricalReplayCandidate pending, HistoricalReplayQuote optionQuote) => Blocked(frame) ? null : policy.AssessDeferredEntry(frame, pending, optionQuote);
    public IReadOnlyList<HistoricalReplayStopUpdate> EvaluateStopUpdates(HistoricalReplayPortfolioFrame frame) => policy.EvaluateStopUpdates(frame);
    public IReadOnlyList<HistoricalReplayPartialExit> EvaluatePartialExits(HistoricalReplayPortfolioFrame frame) =>
        policy.EvaluatePartialExits(frame).Where(value => !Forced(frame, value.Symbol)).ToArray();
    public IReadOnlyList<HistoricalReplayAddOn> EvaluateAddOns(HistoricalReplayPortfolioFrame frame) => Blocked(frame) ? [] :
        policy.EvaluateAddOns(frame).Where(value => !Forced(frame, value.Symbol)).ToArray();
    public bool ShouldExtendTarget(HistoricalReplayPortfolioFrame frame, HistoricalReplayOpenPosition position) =>
        !Forced(frame, position.Symbol) && policy.ShouldExtendTarget(frame, position);
    public IReadOnlyList<HistoricalReplayExitRequest> EvaluateExits(HistoricalReplayPortfolioFrame frame)
    {
        var exits = policy.EvaluateExits(frame).ToDictionary(value => value.Symbol, StringComparer.Ordinal);
        foreach (var position in frame.OpenPositions)
        {
            if (!Fresh(frame, position.Symbol)) continue;
            var reason = frame.Controls.ManualExitRequested(position) ? "ManualExit" :
                market == "NIFTY" && frame.Controls.NiftyKillSwitch ? "EmergencyKillSwitch" : null;
            if (reason is not null) exits[position.Symbol] = new(position.Symbol, frame.Market.AvailableAtUtc, reason);
        }
        return exits.Values.OrderBy(value => value.Symbol, StringComparer.Ordinal).ToArray();
    }
    private bool Forced(HistoricalReplayPortfolioFrame frame, string symbol) =>
        frame.OpenPositions.Any(value => value.Symbol == symbol && frame.Controls.ManualExitRequested(value)) ||
        market == "NIFTY" && frame.Controls.NiftyKillSwitch;
    private bool Fresh(HistoricalReplayPortfolioFrame frame, string symbol) =>
        frame.Market.LatestQuotes.TryGetValue(symbol, out var quote) && quote.LastPrice > 0m &&
        quote.IsFreshAsOf(frame.Market.AvailableAtUtc, TimeSpan.FromSeconds(maximumQuoteAgeSeconds));
}
