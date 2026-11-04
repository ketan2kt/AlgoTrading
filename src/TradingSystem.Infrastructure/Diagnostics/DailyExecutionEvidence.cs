using System.Text.Json;
using TradingSystem.Application.Diagnostics;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Execution;

namespace TradingSystem.Infrastructure.Diagnostics;

internal static class DailyExecutionEvidence
{
    internal sealed record Observation(Guid SourceId, DateTimeOffset At, string Event,
        decimal? Fill, int? Quantity, decimal? PaperPrice = null, decimal? RequestedQuantity = null, string? Reason = null);

    internal static Observation? Parse(DateTimeOffset at, string outcome, string json)
    {
        var kind = outcome.StartsWith("LiveParityException:", StringComparison.Ordinal) ? "PolicySkip" :
            outcome.StartsWith("LiveParityMissed:", StringComparison.Ordinal) ? "Missed" :
            outcome.StartsWith("LiveEntryDecision:", StringComparison.Ordinal) ? "Discovery" : null;
        if (kind is null && !outcome.StartsWith("LiveExecution:", StringComparison.Ordinal)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (kind is not null && Guid.TryParseExact(outcome[(outcome.IndexOf(':')+1)..], "N", out var skipped) &&
                root.ValueKind == JsonValueKind.Object && root.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String)
                return new(skipped, at, kind, null, null, Reason:reason.GetString());
            if (kind is not null) return null;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("sourceIdCompact", out var id) || id.ValueKind != JsonValueKind.String ||
                !Guid.TryParse(id.GetString(), out var source)) return null;
            root.TryGetProperty("details", out var d);
            decimal? Number(string name) => d.ValueKind == JsonValueKind.Object && d.TryGetProperty(name, out var n) &&
                n.ValueKind == JsonValueKind.Number && n.TryGetDecimal(out var value) ? value : null;
            return new(source, at, outcome["LiveExecution:".Length..], Number("AverageFillPrice"),
                d.ValueKind == JsonValueKind.Object && d.TryGetProperty("FilledQuantity", out var q) &&
                q.ValueKind == JsonValueKind.Number && q.TryGetInt32(out var v) ? v : null,
                Number("paperPrice"), Number("paperQuantity"));
        }
        catch (JsonException) { return null; }
    }

    internal static DailyExecutionReview Review(DailyTradingTrade trade, LiveExecutionIntent? intent,
        IEnumerable<Observation> events, string? routeAtFill = null, DateTimeOffset? paperFillAt = null)
    {
        static double? Seconds(DateTimeOffset? from, DateTimeOffset? to) =>
            from.HasValue && to.HasValue && to >= from ? Math.Round((to.Value - from.Value).TotalSeconds, 1) : null;
        var entryDecision = EntryObservation(events);
        DailyExecutionReview Row(string finding, DateTimeOffset? at = null) =>
            new(trade.TradeId, trade.Market, trade.Symbol, intent?.Status ?? "No live intent", finding, at,
                Seconds(paperFillAt ?? (trade.SignalId.HasValue ? null : trade.OpenedAtUtc), intent?.CreatedAtUtc), Seconds(intent?.CreatedAtUtc, intent?.SubmittedAtUtc))
            { EntryRoute = intent is null ? null : (intent.SubmittedAtUtc.HasValue ?
                "Live intent created; broker submission recorded." : "Live intent created; submission not recorded.") +
                (entryDecision is null ? "" : $" Observed: {entryDecision.Reason}") };
        if (intent is null)
        {
            var latest = entryDecision;
            if (latest is not null) return Row($"Recorded {(latest.Event == "PolicySkip" ? "policy exclusion" : "discovery decision")}: {latest.Reason}", latest.At);
            return Row(routeAtFill ?? "No live intent recorded; not proof of an execution failure.", routeAtFill is null ? null : paperFillAt);
        }
        bool entry = false, exit = false;
        foreach (var e in events.OrderBy(x => x.At))
        {
            if (e.Event == "EntryFilledAndProtected")
            {
                if (e.Fill is not > 0 || e.Quantity is null) return Row("Entry fill evidence incomplete.", e.At);
                if (e.Quantity != intent.Quantity) return Row($"Entry filled {e.Quantity} of {intent.Quantity} requested.", e.At);
                if (e.Fill != trade.EntryPrice) return Row(FormattableString.Invariant($"Entry price differed: paper {trade.EntryPrice:0.00}, broker {e.Fill:0.00}."), e.At);
                entry = true;
            }
            else if (e.Event is "AddOnOutcome" or "PartialExitOutcome")
            {
                if (e.Quantity != e.RequestedQuantity)
                    return Row($"{e.Event}: broker filled {e.Quantity} of {e.RequestedQuantity} requested.", e.At);
                if (e.Fill is not > 0 || e.PaperPrice is not > 0)
                    return Row($"{e.Event}: comparable fill evidence is missing.", e.At);
                if (e.Fill != e.PaperPrice)
                    return Row(FormattableString.Invariant($"{e.Event}: paper {e.PaperPrice:0.00}, broker {e.Fill:0.00}."), e.At);
            }
            else if (e.Event is "ReconciliationRequired" or "FinalExitResidualProtected" or "BrokerStopFilledBeforePaperExit")
                return Row($"Execution event: {e.Event}.", e.At);
            else if (e.Event is "PaperExitFilled" or "PaperExitRecovered" or "BrokerStopFilled")
            {
                if (trade.ClosedAtUtc is null || e.Fill is not > 0) return Row("Final fill cannot be compared to a closed paper trade.", e.At);
                if (e.Fill != trade.ExitOrCurrentPrice) return Row(FormattableString.Invariant($"Final exit differed: paper {trade.ExitOrCurrentPrice:0.00}, broker {e.Fill:0.00}."), e.At);
                exit = true;
            }
        }
        if (intent.Status == "Rejected" && !entry) return Row("Live intent was created; recorded status: rejected before a confirmed entry fill.");
        if (!entry || trade.ClosedAtUtc.HasValue && !exit)
            return Row("Incomplete entry/exit evidence; inspect the intent status.");
        return Row("No difference found in recorded entry/final fill prices. Intermediate actions are not certified.");
    }

    internal static Observation? EntryObservation(IEnumerable<Observation> events) =>
        events.Where(e => e.Event is "Discovery" or "Missed" or "PolicySkip")
            .OrderBy(e => e.Reason == AutomaticLiveExecutionService.ExpiredObservation)
            .ThenByDescending(e => e.At).FirstOrDefault();
}
