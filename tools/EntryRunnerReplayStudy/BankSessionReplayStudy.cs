using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using TradingSystem.Application.Execution;
using TradingSystem.Domain.Trading;
using TradingSystem.Application.Strategies;
using TradingSystem.Application.MarketData;

// Offline chronological comparison using the real Bank Nifty signal, admission,
// contract selector and position policies, not the original day's entries.
internal static class BankSessionReplayStudy
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public sealed record Settings(string[] Dates, string ContractPrefix, DateOnly ContractExpiry,
        int LotSize, int Lots, decimal TickSize, decimal StopPercent);

    public static void Signals(string directory,string date,string output)
    {
        using var doc=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"chart-inputs.json")));
        var history=doc.RootElement.GetProperty("bank-nifty").GetProperty("candles").EnumerateArray()
            .Where(c=>c.GetProperty("isClosed").GetBoolean()).Select(c=>new StrategyPriceBar(c.GetProperty("openTimeUtc").GetDateTimeOffset(),
                c.GetProperty("open").GetDecimal(),c.GetProperty("high").GetDecimal(),c.GetProperty("low").GetDecimal(),c.GetProperty("close").GetDecimal()))
            .OrderBy(b=>b.OpenTimeUtc).ToArray();
        var day=DateOnly.ParseExact(date,"yyyy-MM-dd",CultureInfo.InvariantCulture);
        DateOnly Day(StrategyPriceBar b)=>DateOnly.FromDateTime(b.OpenTimeUtc.ToOffset(TimeSpan.FromHours(5.5)).DateTime);
        var all=history.Where(b=>Day(b)==day).ToArray();
        var previous=history.Where(b=>Day(b)<day).GroupBy(Day).OrderByDescending(g=>g.Key).FirstOrDefault()?.ToArray() ?? [];
        var rows=new List<object>();
        for(var count=12;count<=all.Length;count++)
        {
            var bars=all.Take(count).ToArray();
            var recent=bars.TakeLast(21).ToArray();
            var closes=bars.Select(b=>b.Close).ToArray();
            var fast=TechnicalIndicators.ExponentialMovingAverage(closes,9);
            var slow=TechnicalIndicators.ExponentialMovingAverage(closes,Math.Min(21,closes.Length));
            var a=recent.TakeLast(15).ToArray();
            var atr=a.Skip(1).Select((b,i)=>Math.Max(b.High-b.Low,Math.Max(Math.Abs(b.High-a[i].Close),Math.Abs(b.Low-a[i].Close)))).Average();
            var candles=bars.Select(b=>new Candle(new Guid(unchecked((int)(b.OpenTimeUtc.UtcTicks/TimeSpan.TicksPerMinute)),0,0,0,0,0,0,0,0,0,1),Guid.Empty,b.OpenTimeUtc,60,b.Open,b.High,b.Low,b.Close,0,"OfflineNative",null)).ToArray();
            var current=IndexMomentumBreakoutPolicy.EvaluateBankNifty(candles);
            var old=IndexMomentumBreakoutPolicy.Evaluate(candles);
            var gate=current.Direction is {} direction?BankIndexPreQuoteGatePolicy.Evaluate(bars,previous,direction,atr,bars.Take(3).Max(b=>b.High),bars.Take(3).Min(b=>b.Low)):null;
            var structure=current.Direction is {} side?BankNiftyEntryStructurePolicy.Evaluate(bars,side,atr,60,bars[^1].OpenTimeUtc.AddMinutes(1)):null;
            rows.Add(new{at=bars[^1].OpenTimeUtc.AddMinutes(1),fast,slow,atr,current,old,gate,structure});
        }
        File.WriteAllText(output,JsonSerializer.Serialize(rows,JsonOptions));
    }

    public static void Run(string directory, string label, string output)
    {
        var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path.Combine(directory,"bank-study-settings.json")))!;
        using var charts = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"chart-inputs.json")));
        var allBars = charts.RootElement.GetProperty("bank-nifty").GetProperty("candles").EnumerateArray()
            .Where(c => c.GetProperty("isClosed").GetBoolean()).Select(c => new HistoricalReplayBar(
                c.GetProperty("openTimeUtc").GetDateTimeOffset(),60,c.GetProperty("open").GetDecimal(),
                c.GetProperty("high").GetDecimal(),c.GetProperty("low").GetDecimal(),c.GetProperty("close").GetDecimal())
                {Symbol="BANKNIFTY"}).OrderBy(b=>b.OpenTimeUtc).ToArray();
        var reports = new List<object>();
        foreach(var day in settings.Dates)
        {
            var date = DateOnly.ParseExact(day,"yyyy-MM-dd",CultureInfo.InvariantCulture);
            var start = new DateTimeOffset(date.ToDateTime(new TimeOnly(9,15)),TimeSpan.FromHours(5.5));
            var session = allBars.Where(b=>b.OpenTimeUtc>=start&&b.OpenTimeUtc<start.AddHours(6.25)).ToArray();
            if(session.Length<12)continue;
            var quotes=ReadQuotes(directory,day,settings.ContractPrefix);
            // Do not use an instrument first observed later in the day to define
            // the opening universe. Missing opening instruments remain missing.
            var contracts=quotes.Where(q=>q.Symbol.StartsWith(settings.ContractPrefix,StringComparison.Ordinal)&&q.AvailableAtUtc<=start.AddMinutes(12))
                .Select(q=>q.Symbol).Distinct().Select(symbol=>new NiftyOptionContractCandidate(Guid.Empty,symbol,
                    symbol.EndsWith("CE",StringComparison.Ordinal)?InstrumentType.CallOption:InstrumentType.PutOption,
                    settings.ContractExpiry,decimal.Parse(symbol[settings.ContractPrefix.Length..^2],CultureInfo.InvariantCulture),
                    settings.LotSize,settings.TickSize)).ToArray();
            var bars=allBars.Where(b=>b.OpenTimeUtc>=start.AddDays(-4)&&b.AvailableAtUtc<=session[^1].AvailableAtUtc).ToArray();
            var entry=new HistoricalBankNiftySignalPolicy(Guid.Empty,date,60,new TimeOnly(9,15),new TimeOnly(15,0),
                settings.Lots,contracts,settings.StopPercent,30,new TimeOnly(15,15));
            var policy=new HistoricalMarketOptionPositionPolicy(entry,"bank-nifty","BANKNIFTY",
                new(new TimeOnly(15,15),true,true,true,.55m,3,true,.80m,30,5,60,true),contracts);
            var parameters=new HistoricalReplayParameters(.10m,.10m,.0005m,.0005m,375,0)
                {RequireDisplayedFillSize=true,AllowModeledHistoricalCosts=true};
            typeof(HistoricalReplayParameters).GetProperty("AllowObservedBackupBooks",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(parameters,true);
            var evidence=new List<HistoricalReplayEntryEvidence>();
            var began=System.Diagnostics.Stopwatch.StartNew();
            var result=HistoricalPortfolioReplay.Run(bars,quotes,policy,parameters,start,session[^1].AvailableAtUtc,
                exchangeCode:"NSE",entryEvidence:evidence.Add,stopOnExecutionUncertainty:true);
            var complete = contracts.Length > 0 && result.MissingEntryQuoteCount == 0 &&
                result.MissingExitQuoteCount == 0 && result.UnsupportedPartialCount == 0;
            reports.Add(new{date,label,settings,contracts=contracts.Length,quotes=quotes.Length,
                windowStart=start,windowEnd=session[^1].AvailableAtUtc,
                status=contracts.Length==0?"UnsupportedOpeningUniverse":complete?"ModeledWindowCompleted":"IncompleteExecution",
                comparableModeledNet=complete?(decimal?)result.NetPnl:null,elapsedSeconds=began.Elapsed.TotalSeconds,
                result,evidence,policy.CodeVersion});
            Console.WriteLine($"{label} {date}: {result.CompletedPositions.Count} closed, net {result.NetPnl:F2}, missing entry {result.MissingEntryQuoteCount}, exit {result.MissingExitQuoteCount}, {began.Elapsed.TotalSeconds:F1}s");
            File.WriteAllText(output,JsonSerializer.Serialize(new{label,
                applicationSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(HistoricalPortfolioReplay).Assembly.Location))),
                scope="Offline native-chart Bank Nifty chronological policy replay. Opening-observed contract universe with explicitly supplied expiry/lot metadata, no original trade candidates, same modeled costs/slippage and assisted backup books. Missing OI receipts are reported unavailable by the existing policy, not synthesized. Configuration/inputs are not certified to match the original engine; compare arms, not actual ledger parity. Stop on execution uncertainty, not verified full-session P&L.",reports},JsonOptions));
        }
    }

    private static HistoricalReplayQuote[] ReadQuotes(string directory,string date,string prefix)
    {
        var quotes=new List<HistoricalReplayQuote>();
        foreach(var file in Directory.GetFiles(directory,date+"-*.ndjson.gz").Order())
        {
            using var zip=new GZipStream(File.OpenRead(file),CompressionMode.Decompress);
            using var reader=new StreamReader(zip);
            while(reader.ReadLine() is {} line)
            {
                if(string.IsNullOrWhiteSpace(line))continue;
                using var doc=JsonDocument.Parse(line);
                if(!doc.RootElement.TryGetProperty("Observation",out var o)||o.ValueKind!=JsonValueKind.Object)continue;
                var symbol=o.GetProperty("Request").GetProperty("TradingSymbol").GetString()!;
                if(symbol!="BANKNIFTY"&&(!symbol.StartsWith(prefix,StringComparison.Ordinal)||
                    !(symbol.EndsWith("CE",StringComparison.Ordinal)||symbol.EndsWith("PE",StringComparison.Ordinal))))continue;
                var source=o.GetProperty("Source").GetString()!;
                if(source is not ("GrowwFeedSample" or "UpstoxSnapshot"))continue;
                var at=o.GetProperty("ReceivedAtUtc").GetDateTimeOffset();
                var q=o.GetProperty("Quote");
                decimal? Number(string k)=>q.TryGetProperty(k,out var v)&&v.ValueKind==JsonValueKind.Number?v.GetDecimal():null;
                DateTimeOffset? Stamp(string k)=>o.TryGetProperty(k,out var v)&&v.ValueKind==JsonValueKind.String?v.GetDateTimeOffset():null;
                if(!q.TryGetProperty("LastTradeTimeEpochMilliseconds",out var stamp)||stamp.ValueKind!=JsonValueKind.Number)continue;
                var epoch=stamp.GetInt64();
                var sourceAt=DateTimeOffset.FromUnixTimeMilliseconds(epoch<1_000_000_000_000?epoch*1000:epoch);
                if(Number("LastPrice") is not >0||sourceAt>at||Stamp("DepthSourceAtUtc")>at||
                    Number("BidPrice") is <=0||Number("OfferPrice") is <=0||
                    Number("BidPrice") is {} bid&&Number("OfferPrice") is {} ask&&bid>ask)continue;
                quotes.Add(new(symbol,at,Number("LastPrice")!.Value){Source=source,SourceAtUtc=sourceAt,DepthSourceAtUtc=Stamp("DepthSourceAtUtc"),
                    BackupLiquidityAtUtc=Stamp("BackupLiquidityAtUtc"),BidPrice=Number("BidPrice"),OfferPrice=Number("OfferPrice"),
                    BidQuantity=(long?)Number("BidQuantity"),OfferQuantity=(long?)Number("OfferQuantity")});
            }
        }
        return quotes.DistinctBy(q=>(q.Symbol,q.AvailableAtUtc,q.Source)).OrderBy(q=>q,HistoricalQuoteOrderComparer.Instance).ToArray();
    }
}
