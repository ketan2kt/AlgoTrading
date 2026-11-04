using System.Text.Json;
using TradingSystem.Application.Diagnostics;
using TradingSystem.Application.Execution;
using TradingSystem.Domain.Trading;

namespace TradingSystem.Infrastructure.Diagnostics;

internal static class LiveFillPnl
{
    internal enum Leg { Entry, AddOn, Partial, Final }
    internal sealed record Observation(Guid Source, Leg Role, string? Order, int? Qty, decimal? Price, int? Priced);
    internal sealed record PaperLeg(int Qty, decimal Price);

    internal static PaperLeg? ReadPaperLeg(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc=JsonDocument.Parse(json);
            var root=doc.RootElement;
            return root.ValueKind==JsonValueKind.Object && root.TryGetProperty("cumulativeFilledQuantity",out var q) &&
                q.ValueKind==JsonValueKind.Number && q.TryGetInt32(out var units) && units>0 &&
                root.TryGetProperty("averageFillPrice",out var p) && p.ValueKind==JsonValueKind.Number &&
                p.TryGetDecimal(out var price) && price>0 ? new(units,price) : null;
        }
        catch (JsonException) { return null; }
    }

    internal static Observation? Parse(string outcome,string json)
    {
        Leg? role = outcome switch {
            "LiveExecution:EntryFilledAndProtected" => Leg.Entry,
            "LiveExecution:AddOnOutcome" => Leg.AddOn,
            "LiveExecution:PartialExitOutcome" => Leg.Partial,
            "LiveExecution:PaperExitFilled" or "LiveExecution:PaperExitRecovered" or
                "LiveExecution:BrokerStopFilled" or "LiveExecution:BrokerStopFilledBeforePaperExit" or
                "LiveExecution:FinalSellPartFilled" => Leg.Final,
            _ => null
        };
        if (role is null) return null;
        try
        {
            using var doc=JsonDocument.Parse(json);
            var root=doc.RootElement;
            if (root.ValueKind!=JsonValueKind.Object || !root.TryGetProperty("sourceIdCompact",out var s) ||
                s.ValueKind!=JsonValueKind.String || !Guid.TryParse(s.GetString(),out var source)) return null;
            root.TryGetProperty("details",out var d);
            bool Field(string key,out JsonElement v) { v=default; return d.ValueKind==JsonValueKind.Object && d.TryGetProperty(key,out v); }
            int? Units(string key) => Field(key,out var v) && v.ValueKind==JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;
            return new(source,role.Value, Field("BrokerOrderId",out var id) && id.ValueKind==JsonValueKind.String ? id.GetString() : null,
                Units("FilledQuantity"), Field("AverageFillPrice",out var p) && p.ValueKind==JsonValueKind.Number &&
                p.TryGetDecimal(out var price) ? price : null, Units("PricedQuantity"));
        }
        catch (JsonException) { return null; }
    }

    internal static LivePnlComparison Calculate(LiveExecutionIntent? intent, IEnumerable<Observation> observations,
        IReadOnlyDictionary<Leg,PaperLeg> paper)
    {
        static LivePnlComparison Gap(string why) => new("Incomplete",why);
        if (intent is null) return new("No live intent","Paper result only; see the entry-routing evidence.");
        var observed=observations.Where(x=>x.Source==intent.SourceId).Distinct().ToArray();
        if (intent.Status=="Rejected" && intent.FilledQuantity==0 && !observed.Any(x=>x.Qty>0))
            return new("Not filled","Rejected without a recorded fill.");
        if (intent.Status!="Closed") return new(intent.Status=="Protected" ? "Open" : "Incomplete",
            "Not a confirmed closed live position ("+intent.Status+"). Excluded from closed-trade P&L.");
        if (intent.Direction!=Direction.Buy || intent.Market is not ("NIFTY" or "SENSEX" or "BANK-NIFTY"))
            return Gap("Unsupported market or direction.");
        var orders=observed.Where(x=>x.Qty!=0).ToArray();
        if (orders.Any(x=>string.IsNullOrWhiteSpace(x.Order) || x.Qty is not > 0 || x.Price is not > 0 || x.Priced!=x.Qty))
            return Gap("Broker trade prices do not cover the filled quantity, or the order ID is missing.");
        if (orders.GroupBy(x=>x.Order,StringComparer.Ordinal).Any(g=>g.Count()!=1))
            return Gap("Conflicting cumulative evidence for the same broker order.");
        var entries=orders.Where(x=>x.Role==Leg.Entry).ToArray();
        if (entries.Length!=1 || entries[0].Order!=intent.BrokerOrderId || entries[0].Qty>intent.Quantity)
            return Gap("Confirmed original entry is missing or inconsistent.");
        int Size(Leg role) => orders.Where(x=>x.Role==role).Sum(x=>x.Qty!.Value);
        var units=Size(Leg.Entry)+Size(Leg.AddOn);
        if (Size(Leg.AddOn)!=intent.MirroredAddOnQuantity || Size(Leg.Partial)!=intent.MirroredPartialExitQuantity ||
            Size(Leg.Final)==0 || Size(Leg.Final)+Size(Leg.Partial)!=units)
            return Gap("Entry, scaling and closing quantities do not reconcile.");
        static bool Buy(Leg leg) => leg is Leg.Entry or Leg.AddOn;
        var gross=orders.Sum(x=>(Buy(x.Role)?-1:1)*x.Price!.Value*x.Qty!.Value);
        var charges=PaperTradingCostModel.CalculateOptionCharges(orders.Select(x=>new PaperOptionTransaction(
            Buy(x.Role)?PaperOptionTransactionSide.Buy:PaperOptionTransactionSide.Sell,x.Price!.Value,x.Qty!.Value)),
            intent.Market=="SENSEX"?"BSE":"NSE").Total;
        var comparable=orders.All(x=>paper.TryGetValue(x.Role,out var leg) && leg.Price>0 && Size(x.Role)<=leg.Qty);
        decimal? matched=comparable ? orders.Sum(x=>(Buy(x.Role)?-1:1)*paper[x.Role].Price*x.Qty!.Value) : null;
        var reason=!comparable?"Live fills balance; paper actions do not match these units." :
            gross==matched?"No gross execution difference on matched units." :
            gross<matched?"Broker fills earned less than paper on matched units." :
            "Broker fills earned more than paper on matched units.";
        decimal? Difference(bool buy) => comparable ? orders.Where(x=>Buy(x.Role)==buy).Sum(x=>
            (buy?-1:1)*(x.Price!.Value-paper[x.Role].Price)*x.Qty!.Value) : null;
        return new("Closed",reason,units,gross,charges,matched)
        { EntryDifference=Difference(true), ExitDifference=Difference(false) };
    }

    internal static DailyLivePnlSummary Summarize(IEnumerable<LivePnlComparison> results)
    {
        var rows=results.ToArray();
        var closed=rows.Where(r=>r.Status=="Closed").ToArray();
        var paired=closed.Where(r=>r.MatchedPaperGross.HasValue).ToArray();
        return new(closed.Length,paired.Length,rows.Count(r=>r.Status=="No live intent"),rows.Count(r=>r.Status=="Not filled"),
            rows.Count(r=>r.Status=="Open"),rows.Count(r=>r.Status=="Incomplete"),
            closed.Length==0?null:closed.Sum(r=>r.LiveGross),closed.Length==0?null:closed.Sum(r=>r.EstimatedNet),
            paired.Length==0?null:paired.Sum(r=>r.MatchedPaperGross),paired.Length==0?null:paired.Sum(r=>r.ExecutionDifference));
    }
}
