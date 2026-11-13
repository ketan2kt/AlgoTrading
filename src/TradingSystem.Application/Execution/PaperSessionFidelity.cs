namespace TradingSystem.Application.Execution;

public sealed record FidelityAction(string Kind,DateTimeOffset At,int Quantity,decimal Price,string? Reason=null);
public sealed record FidelityTrade(string Symbol,IReadOnlyList<FidelityAction> Actions)
{
    public FidelityAction Entry => Actions.First(x=>x.Kind=="Entry");
}
public sealed record RecordedPaperSession(IReadOnlyList<FidelityTrade> Trades,IReadOnlyList<string> Gaps);
public sealed record PaperReplayPreflight(SessionPolicyReceipt? Receipt, string? CaptureExclusion = null, int? PaperTrades = null);
public interface IRecordedPaperSessionReader
{
    Task<SessionPolicyReceipt?> ReceiptAsync(DateOnly tradingDate,CancellationToken token);
    Task<RecordedPaperSession> ReadTradesAsync(DateOnly tradingDate,CancellationToken token);
    async Task<PaperReplayPreflight> PreflightAsync(DateOnly tradingDate, CancellationToken token) =>
        new(await ReceiptAsync(tradingDate, token));
}
public sealed record FidelityRow(string Symbol,string Kind,string Finding,FidelityAction? Paper,FidelityAction? Replay)
{
    public DateTimeOffset At => Paper is null ? Replay!.At : Replay is null || Paper.At<Replay.At ? Paper.At : Replay.At;
}
public sealed record PaperSessionFidelity(string Verdict,int PaperTrades,int ReplayTrades,
    IReadOnlyList<string> Gaps,IReadOnlyList<FidelityRow> Rows)
{
    public FidelityRow? FirstDifference => Rows.FirstOrDefault(x=>x.Finding!="Match");

    public static PaperSessionFidelity Compare(RecordedPaperSession actual,IReadOnlyList<HistoricalReplayFill> fills)
    {
        var gaps=actual.Gaps.ToList();
        var replay=new List<FidelityTrade>();
        foreach(var g in fills.GroupBy(x=>(x.Symbol,x.DecisionAtUtc,x.EntryAtUtc))){
            var last=g.LastOrDefault(x=>!x.IsPartialExit);
            if(last is null || last.EntryUnits<=0 || last.OriginalEntryPremium is null){
                gaps.Add($"{g.Key.Symbol}: replay entry size, premium or terminal exit missing.");continue;
            }
            var actions=new List<FidelityAction>{new("Entry",last.EntryAtUtc,last.EntryUnits,last.OriginalEntryPremium.Value,last.Strategy)};
            if(last.AddOnAt is { } at && last.AddOnPremium is { } price) actions.Add(new("Add-on",at,last.AddOnUnits,price));
            actions.AddRange(g.Select(x=>new FidelityAction(x.IsPartialExit?"Partial":"Exit",x.ExitAtUtc,x.Quantity,x.ExitPrice,x.IsPartialExit?null:x.ExitReason)));
            replay.Add(new(last.Symbol,actions));
        }
        var rows=new List<FidelityRow>();
        // Match positions before their actions: never pair an exit from another entry.
        // A symmetric one-minute window is a matching aid, not a pass tolerance.
        static bool Near(FidelityTrade a,FidelityTrade b) => a.Symbol==b.Symbol &&
            (a.Entry.At-b.Entry.At).Duration()<=TimeSpan.FromMinutes(1);
        var paired=new HashSet<FidelityTrade>();
        foreach(var p in actual.Trades){
            var candidates=replay.Where(r=>Near(p,r)).ToArray();
            if(candidates.Length==0){rows.Add(new(p.Symbol,"Entry","Paper only",p.Entry,null));continue;}
            if(candidates.Length!=1 || actual.Trades.Count(x=>Near(x,candidates[0]))!=1){
                gaps.Add($"{p.Symbol}: nearby repeated entries cannot be paired uniquely.");rows.Add(new(p.Symbol,"Entry","Ambiguous",p.Entry,null));continue;
            }
            var r=candidates[0];paired.Add(r);
            foreach(var kind in p.Actions.Concat(r.Actions).Select(x=>x.Kind).Distinct(StringComparer.Ordinal)){
                var left=p.Actions.Where(x=>x.Kind==kind).OrderBy(x=>x.At).ToArray();
                var right=r.Actions.Where(x=>x.Kind==kind).OrderBy(x=>x.At).ToArray();
                for(int i=0;i<Math.Max(left.Length,right.Length);i++){
                    var a=left.ElementAtOrDefault(i);var b=right.ElementAtOrDefault(i);
                    var finding=a is null?"Replay only":b is null?"Paper only":a.Quantity!=b.Quantity?"Quantity differs":
                        kind=="Entry" && a.Reason!=b.Reason?"Entry strategy differs":
                        Reason(a.Reason)!=Reason(b.Reason)?"Exit reason differs":a.At!=b.At?"Time differs":
                        a.Price!=b.Price?"Fill price only":"Match";
                    rows.Add(new(p.Symbol,kind,finding,a,b));
                }
            }
        }
        foreach(var r in replay.Where(r=>!paired.Contains(r)))
            rows.Add(new(r.Symbol,"Entry",actual.Trades.Any(x=>Near(x,r))?"Ambiguous":"Replay only",null,r.Entry));
        var ordered=rows.OrderBy(x=>x.At).ThenBy(x=>x.Symbol,StringComparer.Ordinal).ThenBy(x=>x.Kind,StringComparer.Ordinal).ToArray();
        var verdict=gaps.Count>0?"Inconclusive":ordered.Length==0?"No trades on either side; fill behavior untested":
            ordered.All(x=>x.Finding=="Match")?"Recorded actions match":
            ordered.All(x=>x.Finding is "Match" or "Fill price only")?"Same actions; modeled fill prices differ":"Action or timing differences";
        return new(verdict,actual.Trades.Count,replay.Count,gaps.Distinct().ToArray(),ordered);
    }

    private static string? Reason(string? value) => value switch {
        "ObservedStop" or "ObservedBarStop" or "PolicyStop" or "StopLossHit" => "Stop",
        "ObservedTarget" or "ObservedBarTarget" or "TargetHit" => "Target",
        _=>value
    };
}
