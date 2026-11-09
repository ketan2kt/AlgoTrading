using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using TradingSystem.Application.Execution;
using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

// Offline only. This executable can run against the exact pre-change application
// DLL as well as the proposal. Synthetic behavior checks are NOT historical returns.
var root = Path.GetFullPath(args[0]);
var label = args[1];
var output = Path.GetFullPath(args[2]);
var start = new DateTimeOffset(2026, 11, 6, 3, 45, 0, TimeSpan.Zero);
var reports = new List<object>();
var entryChecks = new List<object>();
foreach (var (scenario, regime, quality) in new[]
{
    ("validated-continuation-descriptive-mature", ProfessionalTradeRegime.Exhausted, new PaperEntryQualityDecision(true, ["Validated setup."])),
    ("validated-breakout-descriptive-chop", ProfessionalTradeRegime.Chop, new PaperEntryQualityDecision(true, ["Validated setup."])),
    ("unconfirmed-chop", ProfessionalTradeRegime.Chop, new PaperEntryQualityDecision(false, ["No confirmed structure."])),
    ("ordinary-trend", ProfessionalTradeRegime.Directional, new PaperEntryQualityDecision(true, []))
})
{
    var resolved = regime;
    var permitted = quality.Permitted;
    var type = typeof(ProfessionalTradeFramework).Assembly.GetType("TradingSystem.Application.Execution.EntryChainConsistencyPolicy");
    if (type is not null)
    {
        var decision = type.GetMethod("Evaluate")!.Invoke(null, [regime, quality])!;
        permitted = (bool)decision.GetType().GetProperty("Permitted")!.GetValue(decision)!;
        resolved = (ProfessionalTradeRegime)decision.GetType().GetProperty("PlanRegime")!.GetValue(decision)!;
    }
    var assessment = ProfessionalTradeFramework.Assess(new("nifty", resolved,
        .65m, .85m, 1.5m, .3m, 1000, 1000, 3, null, null, 1.5m, false, 0, true));
    entryChecks.Add(new { scenario, permitted = permitted && assessment.Permitted, planRegime = resolved.ToString() });
}

foreach (var market in new[] { "NIFTY", "SENSEX", "BANKNIFTY" })
foreach (var direction in new[] { Direction.Buy, Direction.Sell })
foreach (var scenario in new[] { "trend-pullback-resumption", "chop", "failed-setup", "trend-signal-premium-reversal" })
{
    var sign = direction == Direction.Buy ? 1m : -1m;
    var bars = Enumerable.Range(0, 60).Select(i =>
    {
        var open = scenario == "chop" ? 22000m + (i % 2 == 0 ? 0 : 8) : 22000m + sign * i * 8m;
        var close = scenario == "chop" ? 22000m + (i % 2 == 0 ? 8 : 0) : open + sign * 8m;
        if (scenario == "trend-pullback-resumption" && i == 26) close -= sign * 6m;
        return new HistoricalReplayBar(start.AddMinutes(i), 60, open,
            Math.Max(open, close) + 1, Math.Min(open, close) - 1, close) { Symbol = market };
    }).ToArray();
    decimal[] prices = scenario switch
    {
        "trend-pullback-resumption" => [100,104,109,109.2m,105.7m,106.5m,112,115,120,130,128,140,139,141,140,139,139,139],
        "chop" => [100,104,108,104,107,101,96,89,91,94,90,90,90,90,90,90,90,90],
        "trend-signal-premium-reversal" => [100,104,109,109.2m,105.7m,104.2m,100,96,89,88,88,88,88,88,88,88,88,88],
        _ => [100,99,95,89,87,86,85,84,84,84,84,84,84,84,84,84,84,84]
    };
    var symbol = market + "SYNTHETIC" + (direction == Direction.Buy ? "CE" : "PE");
    var contract = new NiftyOptionContractCandidate(Guid.Empty, symbol,
        direction == Direction.Buy ? InstrumentType.CallOption : InstrumentType.PutOption,
        new DateOnly(2026, 11, 12), 22000m, 50, .05m);
    var quotes = Enumerable.Range(0, 60).Select(i =>
        new HistoricalReplayQuote(market, start.AddMinutes(i), bars[Math.Max(0, i - 1)].Close))
        .Concat(prices.Select((price, i) => new HistoricalReplayQuote(symbol, start.AddMinutes(i + 22), price)
            { BidPrice = price - .05m, OfferPrice = price + .05m, Source = "SYNTHETIC_BOOK" })).ToArray();
    var candidate = new HistoricalReplayCandidate(symbol, start.AddMinutes(21), "transition setup", .65m, 100)
    {
        LotSize = 50, PlannedStopPrice = 90m, PlannedTargetPrice = 110m,
        UnderlyingDirection = direction,
        SignalRegime = direction == Direction.Buy ? MarketRegime.WeakBullishTrend : MarketRegime.WeakBearishTrend,
        TriggerOnLastPrice = market != "NIFTY", PlannedExitAtUtc = start.AddMinutes(38)
    };
    IHistoricalReplayPortfolioPolicy policy = market == "NIFTY"
        ? new HistoricalNiftyPositionPolicy(new FixedEntry(candidate),
            new(new TimeOnly(10, 23), true, true, true, true, .6m, true, .55m, 3, true, .8m, 30, 30, 60, true))
        : new HistoricalMarketOptionPositionPolicy(new FixedEntry(candidate), market == "SENSEX" ? "sensex" : "bank-nifty", market,
            new(new TimeOnly(10, 23), true, true, true, .55m, 3, true, .8m, 30, 30, 60, true), [contract]);
    var result = HistoricalPortfolioReplay.Run(bars, quotes, policy,
        new(.1m, .1m, .0005m, .0005m, 375, 0) { AllowModeledHistoricalCosts = true },
        start, start.AddMinutes(60), exchangeCode: market == "SENSEX" ? "BSE" : "NSE");
    reports.Add(new
    {
        market, direction = direction.ToString(), scenario,
        comparable = result.MissingEntryQuoteCount == 0 && result.MissingExitQuoteCount == 0 &&
            result.StaleQuoteCount == 0 && result.UnsupportedPartialCount == 0 && result.CompletedPositions.Count > 0,
        result.NetPnl, result.MaximumDrawdown, result.WinRate, result.Expectancy,
        result.TotalCosts, result.TotalSampledProfitGiveback,
        result.MissingEntryQuoteCount, result.MissingExitQuoteCount, result.StaleQuoteCount,
        result.UnsupportedPartialCount, trades = result.Trades
    });
}

const string id = "ED8E8EF3DA552326187F48EE3777F6C678824FE891B5BD5663716CF2A2942638";
var frozen = Path.Combine(root, ".analysis-" + id + ".json.gz");
var coverage = new List<object>();
if (File.Exists(frozen))
{
    if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(frozen))) != id)
        throw new InvalidDataException("Frozen input checksum mismatch.");
    using var gz = new GZipStream(File.OpenRead(frozen), CompressionMode.Decompress);
    var saved = JsonSerializer.Deserialize<HistoricalNormalReplaySavedRun>(gz)!;
    foreach (var slice in saved.Inputs)
    {
        var reference = saved.QuotePaths[$"{slice.Session.Date:yyyy-MM-dd}|{slice.Session.IndexSymbol}"];
        var path = Path.Combine(root, ".analysis-full-replay/runs/quote-paths", reference.Id + ".quotes.gz");
        if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) != reference.Id)
            throw new InvalidDataException("Frozen quote checksum mismatch.");
        var quotes = new List<HistoricalReplayQuote>();
        using var qgz = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
        using var reader = new StreamReader(qgz);
        while (reader.ReadLine() is { } line) quotes.Add(JsonSerializer.Deserialize<HistoricalReplayQuote>(line)!);
        var populated = slice with { Session = slice.Session with { Quotes = quotes } };
        coverage.Add(new { slice.Session.Date, slice.Session.IndexSymbol,
            assessment = HistoricalReplayArchiveCoverageEvaluator.Evaluate(populated, slice.Session.IndexSymbol) });
    }
}
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
File.WriteAllText(output, JsonSerializer.Serialize(new
{
    label, applicationSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(ProfessionalTradeFramework).Assembly.Location))),
    evidence = "SYNTHETIC_BEHAVIOR_REPLAY_NOT_HISTORICAL_PERFORMANCE", entryChecks, reports, historicalCoverage = coverage,
    limitations = "Synthetic quotes/underlying paths, 0.05 premium-point half-spread, 0.05% each-side modeled slippage and modeled charges. Fixed entries isolate exits; no historical strategy expectancy or recovered missed trades is claimed. Historical coverage gates are unchanged."
}, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(output);

sealed class FixedEntry(HistoricalReplayCandidate candidate) : IHistoricalReplayPortfolioPolicy
{
    private bool emitted;
    public string CodeVersion => "fixed-entry-diagnostic-only";
    public IReadOnlyList<HistoricalReplayCandidate> Evaluate(HistoricalReplayPortfolioFrame frame)
    {
        if (emitted || frame.Market.AvailableAtUtc < candidate.DecidedAtUtc) return [];
        emitted = true;
        return [candidate with { DecidedAtUtc = frame.Market.AvailableAtUtc }];
    }
}
