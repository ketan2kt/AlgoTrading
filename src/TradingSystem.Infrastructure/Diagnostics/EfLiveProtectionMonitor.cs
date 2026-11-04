using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TradingSystem.Application.Diagnostics;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.Infrastructure.Diagnostics;

internal sealed class EfLiveProtectionMonitor(TradingDbContext db,TimeProvider clock):ILiveProtectionMonitor
{
    private static readonly string[] Markets=["NIFTY","SENSEX","BANK-NIFTY","BANKNIFTY"];
    public Task<ProtectionMonitorReport> ReadAsync(CancellationToken token)
        => db.Database.CreateExecutionStrategy().ExecuteAsync(()=>ReadSnapshotAsync(token));
    private async Task<ProtectionMonitorReport> ReadSnapshotAsync(CancellationToken token)
    {
        var now=clock.GetUtcNow();
        // Use one consistent database snapshot across intent, paper and receipt reads.
        await using var tx=db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.RepeatableRead,token):null;
        var intents=await db.LiveExecutionIntents.AsNoTracking().Where(i=>Markets.Contains(i.Market) &&
            i.Status!="Closed" && (i.FilledQuantity>0 || (i.Status!="Rejected" && i.Status!="Pending")))
            .ToListAsync(token);
        if(intents.Count==0)return new(now,0,[]);
        var ids=intents.Select(i=>i.SourceId).ToArray();
        var instruments=intents.Select(i=>i.InstrumentId).Distinct().ToArray();
        var names=await db.Instruments.AsNoTracking().Where(i=>instruments.Contains(i.Id))
            .ToDictionaryAsync(i=>i.Id,i=>i.TradingSymbol,token);
        var positions=await db.MarketPaperPositions.AsNoTracking().Where(p=>ids.Contains(p.Id)).ToDictionaryAsync(p=>p.Id,token);
        var closedSignals=await db.PaperTradeResults.AsNoTracking().Where(p=>ids.Contains(p.SignalId)).Select(p=>p.SignalId).ToListAsync(token);
        var outcomes=ids.SelectMany(id=>new[]{"LiveProtectionDesired:"+id.ToString("N"),"PaperStopSize:"+id.ToString("N"),
            "TrailingStop:"+id.ToString("N")}).ToArray();
        var since=intents.Min(i=>i.CreatedAtUtc).AddHours(-1);
        // Source-keyed strategy revisions can precede live entry. Live receipts also must match the intent ID.
        var desiredRows=await db.MarketStrategyAudits.AsNoTracking().Where(a=>outcomes.Contains(a.Outcome))
            .Select(a=>new {a.Outcome,a.CandleTimeUtc,a.ReasonsJson}).ToListAsync(token);
        var desired=desiredRows.SelectMany(a=>StopComparison.Parse(a.CandleTimeUtc,a.Outcome,a.ReasonsJson)).ToLookup(p=>p.Source);
        var audits=await db.MarketStrategyAudits.AsNoTracking().Where(a=>instruments.Contains(a.UnderlyingInstrumentId) &&
            a.CandleTimeUtc>=since && a.Outcome.StartsWith("LiveExecution:"))
            .Select(a=>new {a.CandleTimeUtc,a.Outcome,a.ReasonsJson}).ToListAsync(token);
        var evidence=audits.SelectMany(a=>Receipt(a.CandleTimeUtc,a.Outcome,a.ReasonsJson)).ToLookup(x=>x.Intent,x=>x.Point);
        string[] roles=["ENTRY","ADDON","PARTIAL","EXIT"];
        var refs=intents.Where(i=>i.SourceType=="NIFTY").SelectMany(i=>roles.Select(r=>$"{i.SourceId:N}-{r}")).ToArray();
        var journal=await db.PaperBrokerEvents.AsNoTracking().Where(e=>e.EventType=="OrderFilled" && refs.Contains(e.ClientReference))
            .Select(e=>new {e.ClientReference,e.Sequence,e.PayloadJson,e.OccurredAtUtc}).ToListAsync(token);
        var latestFills=journal.GroupBy(e=>e.ClientReference).ToDictionary(g=>g.Key,g=>g.MaxBy(e=>e.Sequence)!);
        var rows=new List<ProtectionMonitorRow>();
        foreach(var i in intents){
            decimal? stop=null; int? units=null;DateTimeOffset? revision=null;
            if(i.SourceType=="MARKET_POSITION" && positions.TryGetValue(i.SourceId,out var p)){
                units=p.RemainingQuantity;stop=p.StopLoss;
                revision=desired[i.SourceId].Select(x=>(DateTimeOffset?)x.At).Max();
            }else if(i.SourceType=="NIFTY"){
                var group=desired[i.SourceId].OrderByDescending(x=>x.At).GroupBy(x=>x.At).FirstOrDefault();
                if(group is not null && group.Select(x=>(x.Price,x.Qty)).Distinct().Count()==1){
                    var d=group.First();stop=d.Price;units=d.Qty;revision=d.At;
                }
                int held=0;bool complete=latestFills.ContainsKey($"{i.SourceId:N}-ENTRY");
                foreach(var role in roles){
                    if(!latestFills.TryGetValue($"{i.SourceId:N}-{role}",out var fill))continue;
                    var leg=LiveFillPnl.ReadPaperLeg(fill.PayloadJson);
                    if(leg is null){complete=false;continue;}
                    held+=role is "ENTRY" or "ADDON"?leg.Qty:-leg.Qty;
                    if(revision is null || fill.OccurredAtUtc>revision)revision=fill.OccurredAtUtc;
                }
                units=complete && held>=0?held:null;
                if(closedSignals.Contains(i.SourceId))units=0;
            }
            rows.Add(Assess(i,names.GetValueOrDefault(i.InstrumentId)??"(unknown contract)",stop,units,revision,evidence[i.Id],now));
        }
        return new(now,rows.Count(r=>r.NeedsAttention),rows.OrderByDescending(r=>r.NeedsAttention)
            .ThenBy(r=>r.Market).ThenBy(r=>r.Symbol).ThenBy(r=>r.Id).ToArray());
    }

    private static IEnumerable<(Guid Intent,StopComparison.Point Point)> Receipt(DateTimeOffset at,string outcome,string json)
    {
        try{
            using var doc=JsonDocument.Parse(json);
            if(doc.RootElement.ValueKind!=JsonValueKind.Object || !doc.RootElement.TryGetProperty("intentId",out var v) ||
                v.ValueKind!=JsonValueKind.String || !v.TryGetGuid(out var id))return [];
            var points=StopComparison.Parse(at,outcome,json);
            if(!doc.RootElement.TryGetProperty("details",out var details) || details.ValueKind!=JsonValueKind.Object)
                return [];
            return points.Select(p=>{
                if(p.Type!=StopComparison.Kind.Ack)return (id,p);
                // A historical/legacy receipt with an ambiguous broker status is not a verified working stop.
                foreach(var key in new[]{"verifiedStop","reservedStop"}){
                    if(details.TryGetProperty(key,out var s) && s.ValueKind==JsonValueKind.Object &&
                        s.TryGetProperty("BrokerProtectionId",out var order) && order.ValueKind==JsonValueKind.String &&
                        order.GetString()==p.Order && s.TryGetProperty("UpdatedAtUtc",out var stamp) &&
                        stamp.ValueKind==JsonValueKind.String && stamp.TryGetDateTimeOffset(out var time) && time==p.At &&
                        s.TryGetProperty("Status",out var status) && status.ValueKind==JsonValueKind.String &&
                        status.GetString() is "OPEN" or "TRIGGER_PENDING")return (id,p);
                }
                return (id,p with {Type=StopComparison.Kind.Uncertain});
            }).ToArray();
        }catch(JsonException){return [];}
    }

    internal static ProtectionMonitorRow Assess(LiveExecutionIntent i,string symbol,decimal? stop,int? units,
        DateTimeOffset? revision,IEnumerable<StopComparison.Point> points,DateTimeOffset now)
    {
        var records=points.Where(p=>p.Source==i.SourceId).ToArray();
        var latest=records.Where(p=>p.Type==StopComparison.Kind.Ack && p.Order==i.ProtectionId && p.Order!=null)
            .OrderByDescending(p=>p.At).GroupBy(p=>p.At).FirstOrDefault();
        var ack=latest?.First();
        string finding;
        if(i.Status=="ReconciliationRequired")finding="Reconciliation required";
        else if(i.Status=="ResidualProtected" || i.Status=="ProtectedScaleMismatch")finding="Live remainder differs from paper";
        else if(i.Status!="Protected")finding=i.FilledQuantity==0?"Entry outcome pending":"Protection or exit pending";
        else if(!string.IsNullOrWhiteSpace(i.LastError))finding="Execution discrepancy recorded";
        else if(units==0)finding="Paper closed; live exit outstanding";
        else if(ack==null)finding="No confirmation for current stop order";
        else if(ack.At>now || latest!.Select(p=>(p.Price,p.Qty)).Distinct().Count()!=1 ||
            records.Any(p=>p.At>=ack.At && (p.Type==StopComparison.Kind.Uncertain ||
                (p.Type==StopComparison.Kind.Fill && p.Order==ack.Order))))finding="Stop confirmation uncertain";
        else if(ack.Qty!=i.FilledQuantity || (units.HasValue && units!=i.FilledQuantity))finding="Quantity mismatch";
        else if(stop.HasValue && ack.Price!=stop)finding="Stop price mismatch";
        else if(stop is not >0 || units is not >0)finding="Paper stop or size unknown";
        else if(revision>ack.At)finding="Newer strategy revision pending";
        else if(ack.At.ToOffset(TimeSpan.FromHours(5.5)).Date!=now.ToOffset(TimeSpan.FromHours(5.5)).Date)
            finding="Prior-session confirmation only";
        else finding="Last confirmed match";
        return new(i.Id,i.Market,symbol,i.Status,units,i.FilledQuantity>0?i.FilledQuantity:null,stop,i.ProtectionId,
            ack?.Qty,ack?.Price,ack?.At,finding,finding!="Last confirmed match");
    }
}
