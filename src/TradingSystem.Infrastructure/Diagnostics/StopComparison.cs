using System.Text.Json;
using TradingSystem.Application.Diagnostics;

namespace TradingSystem.Infrastructure.Diagnostics;

internal static class StopComparison
{
    internal enum Kind { Desired, Ack, Fill, Uncertain }
    internal sealed record Point(Guid Source, DateTimeOffset At, Kind Type, decimal? Price, int? Qty, string? Order = null,
        DateTimeOffset? CallStarted = null);

    // Only compare facts persisted for this paper source. Legacy records lacking
    // size or a verified snapshot never acquire synthetic confirmations.
    internal static IReadOnlyList<Point> Parse(DateTimeOffset at, string outcome, string json)
    {
        var desired = outcome.StartsWith("LiveProtectionDesired:",StringComparison.Ordinal) ||
            outcome.StartsWith("PaperStopSize:",StringComparison.Ordinal) ||
            outcome.StartsWith("TrailingStop:",StringComparison.Ordinal);
        var opened = outcome == "PaperPositionOpened";
        if (!desired && !opened && !outcome.StartsWith("LiveExecution:",StringComparison.Ordinal)) return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return [];
            if (opened && Guid.TryParse(Text(root,"positionId"),out var position) &&
                Timestamp(root,"paperEntryRecordedAtUtc") is { } recordedAt && Num(root,"initialStop") is > 0m)
                return [new(position,recordedAt,Kind.Desired,Num(root,"initialStop"),Size(root,"quantity"))];
            if (desired && Guid.TryParseExact(outcome[(outcome.IndexOf(':')+1)..],"N",out var source))
                return [new(source,at,Kind.Desired,Num(root,"effectiveStop") ?? Num(root,"newStop"),Size(root,"remainingQuantity"))];
            if (!Guid.TryParse(Text(root,"sourceIdCompact"),out var id) ||
                !root.TryGetProperty("details",out var details) || details.ValueKind != JsonValueKind.Object) return [];
            var points = new List<Point>(2);
            ReadAck("reservedStop"); ReadAck("verifiedStop");
            if (outcome is "LiveExecution:BrokerStopFilled" or "LiveExecution:BrokerStopFilledBeforePaperExit")
                points.Add(new(id,Timestamp(details,"UpdatedAtUtc") ?? at,Kind.Fill,Num(details,"AverageFillPrice"),
                    Size(details,"FilledQuantity"),Text(details,"BrokerOrderId")));
            if (outcome == "LiveExecution:ReconciliationRequired")
                points.Add(new(id,at,Kind.Uncertain,null,null));
            return points;

            void ReadAck(string key)
            {
                if (details.TryGetProperty(key,out var ack) && ack.ValueKind == JsonValueKind.Object &&
                    Text(ack,"BrokerProtectionId") is { Length: > 0 } brokerId &&
                    Timestamp(ack,"UpdatedAtUtc") is { } verifiedAt && Num(ack,"StopLoss") is > 0m &&
                    Size(ack,"Quantity") is > 0)
                    points.Add(new(id,verifiedAt,Kind.Ack,Num(ack,"StopLoss"),Size(ack,"Quantity"),brokerId,
                        Timestamp(details,"protectionCallStartedAt")));
            }
        }
        catch (JsonException) { return []; }
    }

    private static string? Text(JsonElement e,string k) => e.TryGetProperty(k,out var v) &&
        v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static decimal? Num(JsonElement e,string k) => e.TryGetProperty(k,out var v) &&
        v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var n) ? n : null;
    private static int? Size(JsonElement e,string k) => e.TryGetProperty(k,out var v) &&
        v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;
    private static DateTimeOffset? Timestamp(JsonElement e,string k) => e.TryGetProperty(k,out var v) &&
        v.ValueKind == JsonValueKind.String && v.TryGetDateTimeOffset(out var t) ? t : null;

    internal static IReadOnlyList<StopComparisonRow> Review(Guid source,IEnumerable<Point> evidence)
    {
        var rows = new List<StopComparisonRow>();
        var lastAck = new Dictionary<string,Point>(StringComparer.Ordinal);
        Point? desired = null;
        var matched = false;
        var sawAck = false;
        foreach (var e in evidence.Where(x => x.Source == source).Distinct().OrderBy(x => x.At).ThenBy(x => x.Type))
        {
            if (e.Type == Kind.Desired)
            {
                if (desired is not null && e.Price == desired.Price && e.Qty == desired.Qty) continue;
                if (desired is not null && !matched) Add(null,null,"Superseded before a matching confirmation was recorded.");
                desired = e; matched = false; sawAck = false;
            }
            else if (e.Type == Kind.Ack)
            {
                lastAck[e.Order!] = e;
                sawAck = true;
                var exact = desired?.Price == e.Price && desired?.Qty == e.Qty;
                Add(e,null,desired is null ? "Confirmed; paper revision unavailable." :
                    desired.Price is null || desired.Qty is null ? "Confirmed; paper price or size missing." :
                    exact ? "Confirmed: price and quantity match." : "Confirmed price or quantity differs from paper.",
                    desired is not null && exact && !matched);
                matched |= exact;
            }
            else if (e.Type == Kind.Fill)
            {
                var ack = e.Order is null ? null : lastAck.GetValueOrDefault(e.Order);
                Add(ack,e,ack is null ? "Stop fill; prior confirmation for this order unavailable." :
                    e.Price is not > 0m ? "Stop filled; fill price unavailable." :
                    e.Price < ack.Price ? "Stop filled below its last verified trigger." :
                    e.Price > ack.Price ? "Stop filled above its last verified trigger." : "Stop filled at its last verified trigger.");
                if (e.Order is not null) lastAck.Remove(e.Order);
            }
            else
            {
                Add(null,null,"Reconciliation required; stop state not verified by this event.");
                lastAck.Clear();
            }
        }
        if (desired is not null && !sawAck) Add(null,null,"No subsequent confirmation recorded.");
        return rows;

        void Add(Point? ack,Point? fill,string finding,bool latency = false)
        {
            var split = latency && ack?.CallStarted is { } call && desired!.At <= call && call <= ack.At;
            rows.Add(new(desired?.At,desired?.Price,desired?.Qty,ack?.At,ack?.Price,ack?.Qty,
                ack?.Order ?? fill?.Order, latency ? Math.Round((ack!.At-desired!.At).TotalSeconds,3) : null,
                fill?.At,fill?.Price,fill?.Qty,finding)
            {
                SecondsBeforeCall = split ? Math.Round((ack!.CallStarted!.Value - desired!.At).TotalSeconds,3) : null,
                SecondsInCallAndVerification = split ? Math.Round((ack!.At - ack.CallStarted!.Value).TotalSeconds,3) : null
            });
        }
    }
}
