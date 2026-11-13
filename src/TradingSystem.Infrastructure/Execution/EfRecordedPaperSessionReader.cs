using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using TradingSystem.Application.Execution;
using TradingSystem.Infrastructure.Diagnostics;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.Infrastructure.Execution;

internal sealed class EfRecordedPaperSessionReader(TradingDbContext db,FileHistoricalFeedManifestReader manifests)
    : IRecordedPaperSessionReader
{
    private static DateTimeOffset Time(DateOnly tradingDate,int hour,int minute) =>
        new(tradingDate.ToDateTime(new TimeOnly(hour,minute)),TimeSpan.FromHours(5.5));
    private static readonly string[] EngineKeys=["EngineEvidence.nifty.regular","EngineEvidence.sensex.regular","EngineEvidence.bank-nifty.regular"];

    public async Task<SessionPolicyReceipt?> ReceiptAsync(DateOnly tradingDate,CancellationToken token)
    {
        var end=Time(tradingDate,15,30).ToUniversalTime();
        var receipt=(await manifests.ReadEvidenceAsync(tradingDate,Time(tradingDate,9,15),end,token))?.PolicyReceipt;
        return await VerifyReceiptAsync(tradingDate, end, receipt, token);
    }

    public async Task<PaperReplayPreflight> PreflightAsync(DateOnly tradingDate, CancellationToken token)
    {
        var start = Time(tradingDate, 9, 15).ToUniversalTime();
        var end = Time(tradingDate, 15, 30).ToUniversalTime();
        var manifest = await manifests.ReadEvidenceAsync(tradingDate, start, end, token);
        if (manifest is not null && HistoricalFeedCaptureEvidence.CoversWindow(manifest, tradingDate, start, end))
            return new(await VerifyReceiptAsync(tradingDate, end, manifest.PolicyReceipt, token));

        var nifty = await db.PaperTradeResults.AsNoTracking().CountAsync(r => r.ClosedAtUtc >= start &&
            r.ClosedAtUtc <= end && r.TradingSymbol.StartsWith("NIFTY"), token);
        var others = await db.MarketPaperPositions.AsNoTracking().CountAsync(p =>
            (p.Market == "sensex" || p.Market == "bank-nifty") && p.OpenedAtUtc >= start && p.OpenedAtUtc <= end &&
            !p.Strategy.StartsWith("Hero Zero|") && !p.Strategy.StartsWith("Expansion Pair|"), token);
        var reason = manifest is null ? "No dated recorder manifest was available." :
            $"Recorder capture did not cover the full session: gaps={manifest.HasGaps}; " +
            $"subscribed {manifest.SubscribedAtUtc:O}; completed {manifest.CompletedAtUtc:O}.";
        return new(null, reason, nifty + others);
    }

    private async Task<SessionPolicyReceipt?> VerifyReceiptAsync(DateOnly tradingDate, DateTimeOffset end,
        SessionPolicyReceipt? receipt, CancellationToken token)
    {
        if(receipt?.ValidFor(tradingDate)!=true) return null;
        if(await db.AuditLogs.AsNoTracking().AnyAsync(a=>a.Action=="IndexTradingLotsChanged" &&
            a.EntityType==nameof(TradingSystem.Domain.Trading.ApplicationSetting) && a.EntityId==EfTradingLotConfigurationService.Key &&
            a.OccurredAtUtc>=receipt.ObservedAt && a.OccurredAtUtc<=end,token)) return null;
        return receipt;
    }

    public async Task<RecordedPaperSession> ReadTradesAsync(DateOnly tradingDate,CancellationToken token)
    {
        var start=Time(tradingDate,9,15).ToUniversalTime();var end=Time(tradingDate,15,30).ToUniversalTime();
        var trades=new List<FidelityTrade>();var gaps=new List<string>();
        var heartbeat=await db.SystemEvents.AsNoTracking().Where(e=>EngineKeys.Contains(e.EventType) &&
            e.OccurredAtUtc>=start && e.OccurredAtUtc<=end)
            .Select(e=>new {e.EventType,e.OccurredAtUtc,e.Severity,e.DetailsJson}).ToArrayAsync(token);
        var evaluationCloses = new Dictionary<(string Market, DateTimeOffset StartedAt, DateTimeOffset BarOpen), DateTimeOffset>();
        foreach (var row in heartbeat.Where(row => row.EventType is "EngineEvidence.sensex.regular" or "EngineEvidence.bank-nifty.regular"))
        {
            try
            {
                if (row.DetailsJson is null) continue;
                using var doc = JsonDocument.Parse(row.DetailsJson);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("phase", out var phase) || phase.ValueKind != JsonValueKind.String || !phase.ValueEquals("CompletedCandles") ||
                    !root.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String || !status.ValueEquals("Evaluating") ||
                    !root.TryGetProperty("evidence", out var evidence) || evidence.ValueKind != JsonValueKind.Object ||
                    !evidence.TryGetProperty("OpenTimeUtc", out var open) || open.ValueKind != JsonValueKind.String ||
                    !open.TryGetDateTimeOffset(out var openAt) ||
                    !evidence.TryGetProperty("interval", out var period) || period.ValueKind != JsonValueKind.Number ||
                    !period.TryGetInt32(out var seconds) || seconds is not (60 or 300)) continue;
                var close = openAt.AddSeconds(seconds);
                if (close <= row.OccurredAtUtc)
                    evaluationCloses[(row.EventType == "EngineEvidence.sensex.regular" ? "sensex" : "bank-nifty",
                        row.OccurredAtUtc, openAt)] = close;
            }
            catch (JsonException) { /* Fall back to actual fill timestamps, not an inferred candle interval. */ }
        }
        foreach(var key in EngineKeys){
            var series=heartbeat.Where(e=>e.EventType==key).ToArray();
            var stamps=series.Select(e=>e.OccurredAtUtc).Append(start).Append(end).Order().ToArray();
            if(stamps.Zip(stamps.Skip(1)).Any(p=>p.Second-p.First>TimeSpan.FromMinutes(5)) || series.Any(e=>e.Severity=="Error"))
                gaps.Add($"{key}: fault or gap over five minutes in the recorded regular engine.");
        }
        // Use actual simulated fills, not the earlier signal/candle timestamp.
        var journal=await db.PaperBrokerEvents.AsNoTracking().Where(e=>e.EventType=="OrderFilled" &&
            e.OccurredAtUtc>=start && e.OccurredAtUtc<=end)
            .Select(e=>new {e.ClientReference,e.Sequence,e.OccurredAtUtc,e.PayloadJson}).ToArrayAsync(token);
        var results=await db.PaperTradeResults.AsNoTracking().Where(r=>r.ClosedAtUtc>=start && r.ClosedAtUtc<=end &&
            r.TradingSymbol.StartsWith("NIFTY")).ToDictionaryAsync(r=>r.SignalId,token);
        var executions=journal.Where(e=>e.ClientReference.Length>33 && e.ClientReference[32]=='-' &&
            Guid.TryParseExact(e.ClientReference[..32],"N",out _)).ToLookup(e=>Guid.ParseExact(e.ClientReference[..32],"N"));
        var entryAudits=await db.MarketStrategyAudits.AsNoTracking().Where(a=>a.CandleTimeUtc>=start && a.CandleTimeUtc<=end &&
            (a.Outcome=="PaperPositionOpened" || a.Outcome=="EntryStage:PaperPositionOpened"))
            .Select(a=>new {a.Market,a.CandleTimeUtc,a.ReasonsJson}).ToArrayAsync(token);
        var names=new Dictionary<Guid,string>();
        var times=new Dictionary<Guid,DateTimeOffset>();
        var decisions=new Dictionary<Guid,(DateTimeOffset StartedAt,DateTimeOffset? Close)>();
        // A repeated entry in the same decision candle may have no second EntryStage audit.
        var reasoning=await db.AuditLogs.AsNoTracking().Where(a=>a.Action=="NiftyTradeReasoning" &&
            a.OccurredAtUtc>=start && a.OccurredAtUtc<=end).Select(a=>new {a.EntityId,a.AfterJson}).ToArrayAsync(token);
        foreach(var row in reasoning){
            try{
                using var doc=JsonDocument.Parse(row.AfterJson);
                if(Guid.TryParse(row.EntityId,out var source) && doc.RootElement.ValueKind==JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("StrategyId",out var strategy) && strategy.ValueKind==JsonValueKind.String)
                    names[source]=strategy.GetString()!;
            }
            catch(JsonException){ /* Try the entry-stage record next. */ }
        }
        foreach(var audit in entryAudits)
        {
            try{
                using var doc=JsonDocument.Parse(audit.ReasonsJson);
                var root=doc.RootElement;
                if(root.ValueKind!=JsonValueKind.Object) continue;
                if(root.TryGetProperty("signalId",out var s) && s.ValueKind==JsonValueKind.String && s.TryGetGuid(out var id) &&
                    root.TryGetProperty("strategy",out var n) && n.ValueKind==JsonValueKind.String) names[id]=n.GetString()!;
                if(root.TryGetProperty("positionId",out var p) && p.ValueKind==JsonValueKind.String && p.TryGetGuid(out var pos) &&
                    root.TryGetProperty("paperEntryRecordedAtUtc",out var t) && t.ValueKind==JsonValueKind.String &&
                    t.TryGetDateTimeOffset(out var at))
                {
                    times[pos]=at;
                    if(root.TryGetProperty("evaluationStartedAtUtc",out var evaluated) && evaluated.ValueKind==JsonValueKind.String &&
                        evaluated.TryGetDateTimeOffset(out var began) && began<=at)
                    {
                        decisions[pos]=(began,evaluationCloses.TryGetValue((audit.Market,began,audit.CandleTimeUtc),out var close)
                            ? close : null);
                    }
                }
            }
            catch(JsonException){ /* Affected source will be marked incomplete below. */ }
        }
        foreach(var result in results.Values){
            var actions=new List<FidelityAction>();
            foreach(var g in executions[result.SignalId].GroupBy(e=>e.ClientReference)){
                var kind=g.Key[33..] switch {"ENTRY"=>"Entry","ADDON"=>"Add-on","PARTIAL"=>"Partial","EXIT"=>"Exit",_=>null};
                if(kind is null) continue;
                var fill=g.MaxBy(e=>e.Sequence)!;
                var leg=LiveFillPnl.ReadPaperLeg(fill.PayloadJson);
                if(leg is null){gaps.Add($"{result.TradingSymbol}: unreadable paper {kind} fill.");continue;}
                var reason=kind=="Entry"?names.GetValueOrDefault(result.SignalId):kind=="Exit"?result.ExitReason:null;
                if(kind=="Entry" && reason is null) gaps.Add($"{result.TradingSymbol}: recorded entry strategy unavailable.");
                actions.Add(new(kind,fill.OccurredAtUtc,leg.Qty,leg.Price,reason));
            }
            if(!actions.Any(a=>a.Kind=="Entry")){gaps.Add($"{result.TradingSymbol}: entry journal missing.");continue;}
            if(!actions.Any(a=>a.Kind=="Exit")) gaps.Add($"{result.TradingSymbol}: exit journal missing.");
            var buys=actions.Where(a=>a.Kind is "Entry" or "Add-on").Sum(a=>a.Quantity);
            var sells=actions.Where(a=>a.Kind is "Partial" or "Exit").Sum(a=>a.Quantity);
            if(buys!=result.Quantity || sells!=buys) gaps.Add($"{result.TradingSymbol}: fill sizes do not balance to the closed result.");
            trades.Add(new(result.TradingSymbol,actions));
        }
        if(executions.Any(g=>!results.ContainsKey(g.Key) && g.Any(e=>e.ClientReference.EndsWith("-ENTRY",StringComparison.Ordinal))))
            gaps.Add("Paper entry journal contains a source without a completed Nifty result.");

        var positions=await db.MarketPaperPositions.AsNoTracking().Where(p=>(p.Market=="sensex" || p.Market=="bank-nifty") &&
            p.OpenedAtUtc>=start && p.OpenedAtUtc<=end && !p.Strategy.StartsWith("Hero Zero|") &&
            !p.Strategy.StartsWith("Expansion Pair|")).ToArrayAsync(token);
        var ids=positions.Select(p=>p.ExecutionInstrumentId).Distinct().ToArray();
        var symbols=await db.Instruments.AsNoTracking().Where(i=>ids.Contains(i.Id)).ToDictionaryAsync(i=>i.Id,i=>i.TradingSymbol,token);
        var partials=await db.MarketStrategyAudits.AsNoTracking().Where(a=>a.Outcome.StartsWith("PartialProfit:") &&
            a.CandleTimeUtc>=start && a.CandleTimeUtc<=end).Select(a=>new {a.Outcome,a.CandleTimeUtc}).ToArrayAsync(token);
        foreach(var p in positions){
            if(!symbols.TryGetValue(p.ExecutionInstrumentId,out var symbol)){gaps.Add($"{p.Market}: contract identity missing.");continue;}
            if(!times.TryGetValue(p.Id,out var entryAt)){entryAt=p.OpenedAtUtc;gaps.Add($"{symbol}: exact paper entry receipt unavailable; showing position time.");}
            var actions=new List<FidelityAction>{new("Entry",entryAt,p.Quantity,p.EntryPrice,p.Strategy)};
            if(p.PartialExitQuantity>0){
                var partialTimes=partials.Where(a=>a.Outcome==$"PartialProfit:{p.Id:N}").ToArray();
                if(partialTimes.Length!=1 || p.PartialExitPrice is null) gaps.Add($"{symbol}: partial fill evidence incomplete.");
                else actions.Add(new("Partial",partialTimes[0].CandleTimeUtc,p.PartialExitQuantity,p.PartialExitPrice.Value));
            }
            if(p.ClosedAtUtc is not { } closed || closed>end) gaps.Add($"{symbol}: no terminal exit within session.");
            else actions.Add(new("Exit",closed,p.Quantity-p.PartialExitQuantity,p.CurrentPrice,p.Status));
            trades.Add(decisions.TryGetValue(p.Id,out var decision) ? new(symbol,actions)
                { DecisionBarCloseUtc=decision.Close, EvaluationStartedAtUtc=decision.StartedAt } : new(symbol,actions));
        }
        return new(trades,gaps);
    }
}
