using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using TradingSystem.Application.Execution;
using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

// Reusable, offline-only same-entry comparison. The real replay engine owns
// fill sizing, chronological decisions, partials, costs and uncertainty gates.
// Entries are explicitly seeded from the ledger; subsequent fills remain modeled.
internal static class RecordedEntryReplayStudy
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    internal static void Run(string directory, string date, string label, string output, string? selectedMarket = null, bool entryOnly = false)
    {
        using var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,$"trades-{date}.json")));
        using var charts = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"chart-inputs.json")));
        var trades = report.RootElement.GetProperty("trades").EnumerateArray().Select(t=>t.Clone()).ToList();
        var extra = Path.Combine(directory,$"extra-trades-{date}.json");
        if(File.Exists(extra))
        {
            using var additional=JsonDocument.Parse(File.ReadAllText(extra));
            trades.AddRange(additional.RootElement.EnumerateArray().Select(t=>t.Clone()));
        }
        var rows=new List<object>();
        var day=DateOnly.ParseExact(date,"yyyy-MM-dd",CultureInfo.InvariantCulture);
        var start=new DateTimeOffset(day.ToDateTime(new TimeOnly(9,15)),TimeSpan.FromHours(5.5));
        if(selectedMarket is not (null or "nifty" or "bank-nifty" or "sensex")) throw new ArgumentException("Unsupported market.");
        foreach(var market in selectedMarket is null ? new[]{"nifty","bank-nifty"} : new[]{selectedMarket})
        {
            var underlying=market.Replace("-","",StringComparison.Ordinal).ToUpperInvariant();
            var bars=charts.RootElement.GetProperty(market).GetProperty("candles").EnumerateArray()
                .Where(c=>c.GetProperty("isClosed").GetBoolean()).Select(c=>new HistoricalReplayBar(
                    c.GetProperty("openTimeUtc").GetDateTimeOffset(),60,c.GetProperty("open").GetDecimal(),
                    c.GetProperty("high").GetDecimal(),c.GetProperty("low").GetDecimal(),c.GetProperty("close").GetDecimal())
                    {Symbol=underlying}).Where(b=>b.OpenTimeUtc>=start&&b.OpenTimeUtc<start.AddHours(6.25)).ToList();
            // An explicitly supplied end-of-day chart export can extend the cached
            // snapshot. Do not invent bars or silently continue beyond its last close.
            var tail=Path.Combine(directory,$"{market}-final-bars.txt");
            if(File.Exists(tail))
                foreach(var line in File.ReadLines(tail).Where(l=>!string.IsNullOrWhiteSpace(l)))
                {
                    var p=line.Split(',');
                    var at=DateTimeOffset.Parse($"{date}T{p[0]}:00Z",CultureInfo.InvariantCulture);
                    bars.RemoveAll(b=>b.OpenTimeUtc==at);
                    decimal N(int i)=>decimal.Parse(p[i],CultureInfo.InvariantCulture);
                    bars.Add(new(at,60,N(1),N(2),N(3),N(4)){Symbol=underlying});
                }
            bars=bars.OrderBy(b=>b.OpenTimeUtc).ToList();
            var selected=trades.Where(t=>t.GetProperty("market").GetString()!.Replace("-","")==underlying)
                .Where(t=>!t.GetProperty("strategy").GetString()!.StartsWith("Hero Zero|",StringComparison.Ordinal) &&
                    !t.GetProperty("strategy").GetString()!.StartsWith("Expansion Pair|",StringComparison.Ordinal))
                .DistinctBy(t=>t.GetProperty("tradeId").GetString()).OrderBy(t=>t.GetProperty("openedAtUtc").GetDateTimeOffset()).ToArray();
            var symbols=selected.Select(t=>t.GetProperty("symbol").GetString()!).Append(underlying).ToHashSet(StringComparer.Ordinal);
            var prefix=market=="sensex"?"SENSEX26N19":underlying+"26NOV";
            var allQuotes=entryOnly?[]:BankSessionReplayStudy.ReadQuotes(directory,date,prefix,underlying,symbols);
            foreach(var t in selected)
            {
                var symbol=t.GetProperty("symbol").GetString()!;
                var at=t.GetProperty("openedAtUtc").GetDateTimeOffset();
                var price=t.GetProperty("entryPrice").GetDecimal();
                // MarketPaperPosition.Direction is the option order side (both
                // calls and puts are bought), not the cash-index thesis direction.
                var direction=symbol.EndsWith("CE",StringComparison.Ordinal)?Direction.Buy:Direction.Sell;
                var prior=bars.Where(b=>b.AvailableAtUtc<=at).Select(b=>new StrategyPriceBar(b.OpenTimeUtc,b.Open,b.High,b.Low,b.Close)).ToArray();
                var gate=NiftyCountertrendEntryPolicy.Evaluate(prior,direction,
                    MultiTimeframeDecisionPolicy.Analyze(prior,direction,prior.Take(15).Max(b=>b.High),prior.Take(15).Min(b=>b.Low)),60);
                if(entryOnly)
                {
                    var sensexGate=market=="sensex"?SensexStalledTrendEntryPolicy.Evaluate(
                        t.GetProperty("strategy").GetString()=="Sensex persistent directional thesis",
                        prior,direction,start,at,60):null;
                    rows.Add(new{market,tradeId=t.GetProperty("tradeId").GetString(),symbol,at,
                        actualNet=t.GetProperty("netRealisedPnl").GetDecimal(),entryGate=gate,sensexGate});
                    continue;
                }
                var lot=market=="nifty"?65:market=="sensex"?20:30;
                var quantity=t.GetProperty("quantity").GetInt32();
                var strategy=market=="nifty"?(direction==Direction.Buy?"ema-pullback-continuation":"exploration-ema-pullback-continuation")
                    :t.GetProperty("strategy").GetString()!;
                var plan=SensexOptionAdmissionPolicy.ProtectivePrices(price,10m,.05m);
                var decisionAt=new DateTimeOffset(at.Year,at.Month,at.Day,at.Hour,at.Minute,0,at.Offset);
                var candidate=new HistoricalReplayCandidate(symbol,decisionAt,strategy,.85m,quantity)
                {
                    LotSize=lot,UnderlyingDirection=direction,
                    TriggerOnLastPrice=market!="nifty",
                    RecomputesProtectionFromInitialRisk=market=="nifty",
                    PlannedStopPrice=market=="sensex"?plan.Stop:Math.Floor(price*.9m/.05m)*.05m,
                    PlannedTargetPrice=market=="sensex"?plan.Target:Math.Ceiling(price*1.1m/.05m)*.05m,
                    SignalRegime=market=="nifty"?MarketRegime.LowVolatilityCompression:
                        direction==Direction.Buy?MarketRegime.WeakBullishTrend:MarketRegime.WeakBearishTrend
                };
                var contract=new NiftyOptionContractCandidate(Guid.Empty,symbol,direction==Direction.Buy?InstrumentType.CallOption:InstrumentType.PutOption,
                    new DateOnly(2026,11,market=="sensex"?19:23),decimal.Parse(symbol[prefix.Length..^2],CultureInfo.InvariantCulture),lot,.05m);
                IHistoricalReplayPortfolioPolicy entry=new RecordedEntry(candidate);
                IHistoricalReplayPortfolioPolicy policy=market=="nifty"
                    ?new HistoricalNiftyPositionPolicy(entry,new(new TimeOnly(15,15),true,true,true,true,.60m,true,.55m,3,true,.80m,30,5,60,true),ticks:new Dictionary<string,decimal>{{symbol,.05m}})
                    :new HistoricalMarketOptionPositionPolicy(entry,market,underlying,new(new TimeOnly(15,15),true,true,true,.55m,3,true,.80m,30,5,60,true),[contract]);
                var parameters=new HistoricalReplayParameters(.10m,.10m,.0005m,.0005m,375,0)
                    {RequireDisplayedFillSize=true,AllowModeledHistoricalCosts=true};
                typeof(HistoricalReplayParameters).GetProperty("AllowObservedBackupBooks",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(parameters,true);
                var quotes=allQuotes.Where(q=>q.Symbol==symbol||q.Symbol==underlying).ToArray();
                var evidence=new List<HistoricalReplayEntryEvidence>();
                var result=HistoricalPortfolioReplay.Run(bars,quotes,policy,parameters,start,
                    bars[^1].AvailableAtUtc.AddSeconds(30),entryEvidence:evidence.Add,stopOnExecutionUncertainty:true,
                    exchangeCode:market=="sensex"?"BSE":"NSE",
                    recordedEntry:new(candidate,at,price));
                var complete=result.CompletedPositions.Count==1&&result.MissingEntryQuoteCount==0&&
                    result.MissingExitQuoteCount==0&&result.UnsupportedPartialCount==0;
                rows.Add(new{market,tradeId=t.GetProperty("tradeId").GetString(),symbol,at,
                    actualNet=t.GetProperty("netRealisedPnl").GetDecimal(),entryGate=gate,
                    status=complete?"RecordedEntryModeledExit":"UnresolvedExecution",net=complete?(decimal?)result.NetPnl:null,
                    result.TotalCosts,result.Trades,result.MissingEntryQuoteCount,result.MissingExitQuoteCount,result.UnsupportedPartialCount,evidence,
                    policy.CodeVersion});
                Console.WriteLine($"{label} {market} {at.ToOffset(TimeSpan.FromHours(5.5)):HH:mm}: {(complete?result.NetPnl.ToString("F2",CultureInfo.InvariantCulture):"unresolved")}");
            }
        }
        File.WriteAllText(output,JsonSerializer.Serialize(new{label,date,
            applicationSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(HistoricalPortfolioReplay).Assembly.Location))),
            scope="Original ledger entry price/time/units; production-shared modeled exits. Reconstructed 10% initial stop/target and regime, native chart inputs, observed backup books, 0.05% exit slippage and modeled exchange costs. Not actual-ledger parity or a replacement-entry portfolio. Net excludes unresolved executions. Entry gate is a separate classification, not included in exit-only net.",rows},JsonOptions));
    }
    private sealed class RecordedEntry(HistoricalReplayCandidate candidate):IHistoricalReplayPortfolioPolicy
    {
        private bool emitted;
        public string CodeVersion=>"recorded-entry-comparison-v1";
        public IReadOnlyList<HistoricalReplayCandidate> Evaluate(HistoricalReplayPortfolioFrame frame)
        {
            if(emitted||frame.Market.AvailableAtUtc<candidate.DecidedAtUtc)return [];
            emitted=true;return[candidate with{DecidedAtUtc=frame.Market.AvailableAtUtc}];
        }
    }
}
