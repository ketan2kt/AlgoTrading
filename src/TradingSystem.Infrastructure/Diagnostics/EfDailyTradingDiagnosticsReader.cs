using Microsoft.EntityFrameworkCore;
using TradingSystem.Application.Diagnostics;
using TradingSystem.Application.Execution;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.Infrastructure.Diagnostics;

internal sealed class EfDailyTradingDiagnosticsReader(
    TradingDbContext db,
    TimeProvider timeProvider, MarketData.HistoricalQuoteArchive? quoteArchive = null) : IDailyTradingDiagnosticsReader
{
    private static readonly TimeSpan IndiaOffset = TimeSpan.FromHours(5.5);
    private const int MaximumDecisions = 500;
    private const int MaximumEvents = 100;

    public Task<DailyTradingDiagnosticsReport> ReadWithTimelineAsync(DateOnly tradingDate, CancellationToken token)
        => ReadCoreAsync(tradingDate, "all", true, token);
    public Task<DailyTradingDiagnosticsReport> ReadAsync(DateOnly tradingDate, string market, CancellationToken cancellationToken)
        => ReadCoreAsync(tradingDate, market, false, cancellationToken);
    private async Task<DailyTradingDiagnosticsReport> ReadCoreAsync(
        DateOnly tradingDate,
        string market,
        bool withTimeline, CancellationToken cancellationToken)
    {
        var normalizedMarket = NormalizeMarket(market);
        var selectedMarkets = normalizedMarket == "ALL"
            ? new[] { "NIFTY", "SENSEX", "BANKNIFTY", "BANKEX" }
            : new[] { normalizedMarket };
        var selectedMarketCodes = selectedMarkets.Select(value => value == "BANKNIFTY"
            ? "bank-nifty" : value.ToLowerInvariant()).ToArray();
        var startUtc = new DateTimeOffset(
            tradingDate.ToDateTime(TimeOnly.MinValue), IndiaOffset).ToUniversalTime();
        var endUtc = startUtc.AddDays(1);
        var warnings = new List<string>();

        var underlyings = await db.Instruments.AsNoTracking()
            .Where(value => selectedMarkets.Contains(value.TradingSymbol))
            .Select(value => new { value.Id, value.TradingSymbol })
            .ToListAsync(cancellationToken);
        if (underlyings.Count == 0)
            warnings.Add("No matching index underlying instrument was found.");

        var underlyingIds = underlyings.Select(value => value.Id).ToArray();
        var instrumentMarkets = underlyings.ToDictionary(
            value => value.Id,
            value => value.TradingSymbol.ToUpperInvariant());

        var evaluations = underlyingIds.Length == 0
            ? []
            : await db.StrategyEvaluations.AsNoTracking()
                .Where(value => underlyingIds.Contains(value.InstrumentId) &&
                                value.CandleTimeUtc >= startUtc && value.CandleTimeUtc < endUtc)
                .OrderBy(value => value.CandleTimeUtc)
                .ToListAsync(cancellationToken);
        var audits = await db.MarketStrategyAudits.AsNoTracking()
            .Where(value => (selectedMarkets.Contains(value.Market) ||
                             selectedMarketCodes.Contains(value.Market)) &&
                            value.CandleTimeUtc >= startUtc && value.CandleTimeUtc < endUtc)
            .OrderBy(value => value.CandleTimeUtc)
            .ToListAsync(cancellationToken);

        var decisions = evaluations.Select(value => new DailyTradingDecision(
                instrumentMarkets.GetValueOrDefault(value.InstrumentId, normalizedMarket),
                "StrategyEvaluation",
                value.CandleTimeUtc,
                value.Outcome,
                value.RegimeConfidence,
                value.Regime.ToString(),
                value.RegimeBias?.ToString(),
                value.CurrentPrice,
                Limit(value.FailedConditionsJson),
                value.SignalId))
            .Concat(audits.Where(value => value.Outcome != "ExpansionPair:Observation" &&
                !value.Outcome.StartsWith("LiveExecution:", StringComparison.Ordinal) &&
                !value.Outcome.StartsWith("LiveParityException:", StringComparison.Ordinal) &&
                !value.Outcome.StartsWith("LiveParityMissed:", StringComparison.Ordinal) &&
                !value.Outcome.StartsWith("LiveEntryDecision:", StringComparison.Ordinal) &&
                !value.Outcome.StartsWith("LiveProtectionDesired:", StringComparison.Ordinal) &&
                !value.Outcome.StartsWith("PaperStopSize:", StringComparison.Ordinal) &&
                !value.Outcome.StartsWith("TrailingStop:", StringComparison.Ordinal) &&
                !value.Outcome.StartsWith("PositionSample:", StringComparison.Ordinal))
                .Select(value => new DailyTradingDecision(
                value.Market.ToUpperInvariant(),
                "MarketStrategyAudit",
                value.CandleTimeUtc,
                value.Outcome,
                value.Confidence,
                null,
                null,
                null,
                Limit(value.ReasonsJson),
                null)))
            .OrderBy(value => value.CandleTimeUtc)
            .ToList();

        var paperRows = await (
            from result in db.PaperTradeResults.AsNoTracking()
            join signal in db.Signals.AsNoTracking() on result.SignalId equals signal.Id
            join instrument in db.Instruments.AsNoTracking() on result.InstrumentId equals instrument.Id
            where result.ClosedAtUtc >= startUtc && result.ClosedAtUtc < endUtc &&
                  ((selectedMarkets.Contains("NIFTY") && instrument.TradingSymbol.Contains("NIFTY")) ||
                   (selectedMarkets.Contains("SENSEX") && instrument.TradingSymbol.Contains("SENSEX")))
            orderby result.ClosedAtUtc
            select new { Result = result, Signal = signal, Instrument = instrument })
            .ToListAsync(cancellationToken);

        var marketPositions = await db.MarketPaperPositions.AsNoTracking()
            .Where(value => (selectedMarkets.Contains(value.Market) ||
                             selectedMarketCodes.Contains(value.Market)) &&
                            value.OpenedAtUtc >= startUtc && value.OpenedAtUtc < endUtc)
            .OrderBy(value => value.OpenedAtUtc)
            .ToListAsync(cancellationToken);
        var executionIds = marketPositions.Select(value => value.ExecutionInstrumentId).Distinct().ToArray();
        var executionSymbols = executionIds.Length == 0
            ? new Dictionary<Guid, string>()
            : await db.Instruments.AsNoTracking()
                .Where(value => executionIds.Contains(value.Id))
                .ToDictionaryAsync(value => value.Id, value => value.TradingSymbol, cancellationToken);

        var signalIds = paperRows.Select(value => value.Result.SignalId).Distinct().ToArray();
        var entryReferences = signalIds.Select(id => $"{id:N}-ENTRY").ToArray();
        var entryFills = await (from order in db.Orders.AsNoTracking()
            join fill in db.Trades.AsNoTracking() on order.Id equals fill.OrderId
            where entryReferences.Contains(order.ClientReference)
            select new { order.ClientReference, fill.ExecutedAtUtc }).ToListAsync(cancellationToken);
        var entryTimes = entryFills.GroupBy(fill => fill.ClientReference)
            .ToDictionary(group => group.Key, group => group.Min(fill => fill.ExecutedAtUtc));
        // The durable paper broker journals simulated fills without necessarily
        // creating execution-ledger Trades rows. Use that authoritative fill
        // receipt, not the earlier signal candle, when the ledger is absent.
        var journalFills = await JournalEntryFills(db.PaperBrokerEvents.AsNoTracking(), entryReferences).ToArrayAsync(cancellationToken);
        foreach (var fill in journalFills) entryTimes.TryAdd(fill.ClientReference, fill.At);
        var samples = signalIds.Length == 0
            ? []
            : await db.PaperTradePriceSamples.AsNoTracking()
                .Where(value => signalIds.Contains(value.SignalId))
                .OrderBy(value => value.ObservedAtUtc)
                .ToListAsync(cancellationToken);
        var samplesBySignal = samples.GroupBy(value => value.SignalId)
            .ToDictionary(value => value.Key, value => value.ToArray());

        var trades = new List<DailyTradingTrade>(paperRows.Count + marketPositions.Count);
        foreach (var row in paperRows)
        {
            var hasFillTime = entryTimes.TryGetValue($"{row.Result.SignalId:N}-ENTRY", out var enteredAt);
            if (!hasFillTime)
            {
                enteredAt = row.Signal.MarketDataTimestampUtc;
                warnings.Add($"Trade {row.Result.Id}: actual entry fill timestamp unavailable; displayed entry is signal time.");
            }
            samplesBySignal.TryGetValue(row.Result.SignalId, out var tradeSamples);
            var path = CalculateBoughtOptionExcursionPath(row.Result.EntryPrice, row.Result.Quantity,
                enteredAt, row.Result.ExitPrice,
                row.Result.ClosedAtUtc, tradeSamples ?? []);
            trades.Add(new DailyTradingTrade(
                MarketFromSymbol(row.Instrument.TradingSymbol),
                "PaperTradeResult",
                row.Result.Id,
                row.Result.SignalId,
                row.Result.TradingSymbol,
                "NiftyAutomation",
                row.Signal.Direction.ToString(),
                row.Result.Quantity,
                row.Result.EntryPrice,
                row.Result.ExitPrice,
                enteredAt,
                row.Result.ClosedAtUtc,
                "Closed",
                row.Result.ExitReason,
                row.Result.GrossPnl,
                row.Result.EstimatedCosts,
                row.Result.RealisedPnl,
                path.MaximumFavourableExcursion,
                path.MaximumFavourableExcursionAtUtc,
                path.MaximumAdverseExcursion,
                path.MaximumAdverseExcursionAtUtc,
                path.Coverage,
                path.SampleCount));
        }

        var marketPositionIds = marketPositions.Select(value => value.Id).ToArray();
        var marketPositionAudits = marketPositionIds.Length == 0
            ? []
            : await db.MarketStrategyAudits.AsNoTracking()
                .Where(value => (selectedMarkets.Contains(value.Market) ||
                                 selectedMarketCodes.Contains(value.Market)) &&
                                value.CandleTimeUtc >= startUtc && value.CandleTimeUtc < endUtc &&
                                value.Outcome.StartsWith("PositionSample:"))
                .Select(value => value.ReasonsJson)
                .ToListAsync(cancellationToken);
        var positionSamples = marketPositionAudits
            .Select(SensexResearchAuditParser.ParsePricePoint)
            .Where(value => value.HasValue && marketPositionIds.Contains(value.Value.PositionId))
            .Select(value => value!.Value)
            .GroupBy(value => value.PositionId)
            .ToDictionary(value => value.Key,
                value => value.Select(sample => sample.Point).OrderBy(sample => sample.ObservedAtUtc).ToArray());

        foreach (var position in marketPositions)
        {
            positionSamples.TryGetValue(position.Id, out var samplesForPosition);
            var exitAt = position.ClosedAtUtc ?? timeProvider.GetUtcNow();
            var path = CalculatePositionPath(position, samplesForPosition ?? [], exitAt);
            var hasPersistedPath = position.MaximumFavourablePnl.HasValue &&
                                   position.MaximumAdversePnl.HasValue &&
                                   position.ExcursionSampleCount > 1;
            var maximumFavourable = hasPersistedPath
                ? Math.Max(0m, position.MaximumFavourablePnl!.Value)
                : path.MaximumFavourableExcursion;
            var maximumAdverse = hasPersistedPath
                ? Math.Max(0m, -position.MaximumAdversePnl!.Value)
                : path.MaximumAdverseExcursion;
            var sampleCount = Math.Max(position.ExcursionSampleCount, path.SampleCount);
            var coverage = hasPersistedPath ? "Persisted quote observations"
                : path.SampleCount > 2 ? "Historical sampled path; legacy cadence"
                : "Entry/exit only; historical intratrade path unavailable";
            if (hasPersistedPath && position.ExcursionObservedFromUtc is { } observedFrom &&
                observedFrom > position.OpenedAtUtc)
                coverage = $"Quote observations since {observedFrom:HH:mm:ss} UTC; earlier path unavailable";
            coverage += position.ExcursionBasisVersion == 1
                ? "; excursions are gross before fees"
                : "; legacy excursion accounting may mix gross and net; not suitable for exact giveback comparisons";
            trades.Add(new DailyTradingTrade(
                position.Market.ToUpperInvariant(),
                "MarketPaperPosition",
                position.Id,
                null,
                executionSymbols.GetValueOrDefault(position.ExecutionInstrumentId, position.ExecutionInstrumentId.ToString()),
                position.Strategy,
                position.Direction.ToString(),
                position.Quantity,
                position.EntryPrice,
                position.CurrentPrice,
                position.OpenedAtUtc,
                position.ClosedAtUtc,
                position.Status,
                position.Status == "Active" ? null : position.Status,
                position.RealisedPnl + position.CostsPaid,
                position.CostsPaid,
                position.RealisedPnl,
                maximumFavourable,
                hasPersistedPath ? position.MaximumFavourableAtUtc : path.MaximumFavourableExcursionAtUtc,
                maximumAdverse,
                hasPersistedPath ? position.MaximumAdverseAtUtc : path.MaximumAdverseExcursionAtUtc,
                coverage,
                sampleCount));
        }
        trades.Sort((left, right) => left.OpenedAtUtc.CompareTo(right.OpenedAtUtc));

        var feeds = await db.ProviderHealth.AsNoTracking()
            .OrderBy(value => value.Provider)
            .Select(value => new DailyTradingFeedStatus(
                value.Provider,
                value.IsAvailable,
                value.LastSuccessAtUtc,
                value.LastFailureAtUtc,
                value.ErrorCode,
                value.UpdatedAtUtc))
            .ToListAsync(cancellationToken);

        var events = await db.SystemEvents.AsNoTracking()
            .Where(value => value.OccurredAtUtc >= startUtc && value.OccurredAtUtc < endUtc)
            .OrderByDescending(value => value.OccurredAtUtc)
            .Take(MaximumEvents)
            .Select(value => new DailyTradingSystemEvent(
                value.OccurredAtUtc,
                value.Severity,
                value.EventType,
                value.Message,
                value.DetailsJson))
            .ToListAsync(cancellationToken);

        DateTimeOffset? lastCandleAtUtc = null;
        if (underlyingIds.Length > 0)
        {
            lastCandleAtUtc = await db.Candles.AsNoTracking()
                .Where(value => underlyingIds.Contains(value.InstrumentId) &&
                                value.OpenTimeUtc >= startUtc && value.OpenTimeUtc < endUtc)
                .Select(value => (DateTimeOffset?)value.OpenTimeUtc)
                .MaxAsync(cancellationToken);
        }

        if (decisions.Count == 0)
            warnings.Add("No strategy decision telemetry exists for the selected date and market.");
        if (lastCandleAtUtc is null)
            warnings.Add("No underlying candle exists for the selected date and market.");
        if (trades.Count == 0)
            warnings.Add("No paper trade was recorded for the selected date and market.");
        var pathIncomplete = trades.Count(value => value.ClosedAtUtc is not null &&
            value.ExcursionCoverage.Contains("unavailable", StringComparison.OrdinalIgnoreCase));
        if (pathIncomplete > 0)
            warnings.Add($"{pathIncomplete} completed trade(s) have no persisted intratrade price path; peak/adverse values use only known entry/exit prices and cannot recover historical extremes.");
        if (decisions.Count > MaximumDecisions)
            warnings.Add($"Decision details were limited to the latest {MaximumDecisions}; summary counts cover the full day.");

        var engineCoverage = await EfEngineEvidenceReader.Query(db, tradingDate, market)
            .GroupBy(value => value.EventType)
            .Select(group => new EngineEvidenceCoverage(group.Key, group.Count(),
                group.Min(value => value.OccurredAtUtc), group.Max(value => value.OccurredAtUtc)))
            .ToListAsync(cancellationToken);
        foreach (var marketCode in selectedMarketCodes)
        {
            if (marketCode == "bankex") continue; // Legacy positions, no new entry engine.
            var symbol = marketCode.Replace("-", "", StringComparison.Ordinal).ToUpperInvariant();
            var latestRegular = evaluations.Where(value =>
                    instrumentMarkets.GetValueOrDefault(value.InstrumentId) == symbol)
                .Select(value => (DateTimeOffset?)value.CandleTimeUtc)
                .Concat(audits.Where(value => value.Market.Equals(marketCode, StringComparison.OrdinalIgnoreCase) &&
                    (value.Outcome == "NoSignal" || value.Outcome == "PaperPositionOpened" ||
                     value.Outcome == "DecisionSnapshot:v2"))
                    .Select(value => (DateTimeOffset?)value.CandleTimeUtc)).DefaultIfEmpty().Max();
            var coverageWarning = TradingEvidenceCoveragePolicy.Warning(marketCode, tradingDate,
                latestRegular, engineCoverage.FirstOrDefault(value =>
                    value.EngineKey == $"EngineEvidence.{marketCode}.regular")?.LastAtUtc, lastCandleAtUtc);
            if (coverageWarning is not null) warnings.Add(coverageWarning);
        }

        var outcomeCounts = decisions
            .GroupBy(value => value.Outcome, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(value => value.Count())
            .ToDictionary(value => value.Key, value => value.Count(), StringComparer.OrdinalIgnoreCase);
        var completed = trades.Where(value => value.ClosedAtUtc is not null).ToArray();
        var acceptedDecisionCount = decisions.Count(value => IsAccepted(value.Outcome));
        var summary = new DailyTradingDiagnosticsSummary(
            decisions.Count,
            acceptedDecisionCount,
            decisions.Count - acceptedDecisionCount,
            outcomeCounts,
            completed.Length,
            trades.Count - completed.Length,
            completed.Count(value => value.NetRealisedPnl > 0),
            completed.Count(value => value.NetRealisedPnl < 0),
            completed.Sum(value => value.GrossPnl),
            completed.Sum(value => value.Costs),
            completed.Sum(value => value.NetRealisedPnl),
            decisions.Count == 0 ? null : decisions[0].CandleTimeUtc,
            decisions.Count == 0 ? null : decisions[^1].CandleTimeUtc,
            lastCandleAtUtc);

        var sources = trades.Select(t => t.SignalId ?? t.TradeId).Distinct().ToArray();
        var intents = await db.LiveExecutionIntents.AsNoTracking().Where(i => sources.Contains(i.SourceId))
            .ToDictionaryAsync(i => i.SourceId, cancellationToken);
        var lifecycle = audits.Select(a => DailyExecutionEvidence.Parse(a.CandleTimeUtc, a.Outcome, a.ReasonsJson))
            .OfType<DailyExecutionEvidence.Observation>().ToLookup(e => e.SourceId);
        var refs = signalIds.SelectMany(id => new[] { $"{id:N}-ENTRY", $"{id:N}-ADDON", $"{id:N}-PARTIAL", $"{id:N}-EXIT" }).ToArray();
        var protection = audits.SelectMany(a => StopComparison.Parse(a.CandleTimeUtc,a.Outcome,a.ReasonsJson))
            .ToLookup(p => p.Source);
        var fillRows = await db.PaperBrokerEvents.AsNoTracking().Where(x => x.EventType == "OrderFilled" && refs.Contains(x.ClientReference))
            .Select(x => new {x.ClientReference,x.OccurredAtUtc,x.Sequence,x.PayloadJson}).ToListAsync(cancellationToken);
        var fills = fillRows.GroupBy(x => x.ClientReference).ToDictionary(g => g.Key,g => g.Min(x => x.OccurredAtUtc));
        var journal = fillRows.GroupBy(x=>x.ClientReference).ToDictionary(g=>g.Key,g=>g.MaxBy(x=>x.Sequence)!.PayloadJson);
        var positionMap = marketPositions.ToDictionary(x=>x.Id);
        var economics = audits.Select(a=>LiveFillPnl.Parse(a.Outcome,a.ReasonsJson))
            .OfType<LiveFillPnl.Observation>().ToLookup(x=>x.Source);
        var keys = trades.Where(t => !intents.ContainsKey(t.SignalId ?? t.TradeId))
            .Select(t => DailyLiveRouteHistory.Key(t.Market)).Distinct().ToArray();
        var history = await DailyLiveRouteHistory.ReadAsync(db, keys, fillRows.Count == 0 ? startUtc :
            new[] { startUtc, fillRows.Min(x => x.OccurredAtUtc) }.Min(), endUtc, cancellationToken);
        var reviews = trades.Select(t => {
            var source = t.SignalId ?? t.TradeId;
            DateTimeOffset? fillAt = !t.SignalId.HasValue ? t.OpenedAtUtc :
                fills.TryGetValue($"{source:N}-ENTRY", out var at) ? at : null;
            var route = fillAt is null ? "Paper broker fill timestamp missing; cannot establish switch state." :
                DailyLiveRouteHistory.Explain(t.Market, fillAt.Value, history);
            var paper = new Dictionary<LiveFillPnl.Leg,LiveFillPnl.PaperLeg>();
            if (positionMap.TryGetValue(t.TradeId,out var position))
            {
                paper[LiveFillPnl.Leg.Entry]=new(position.Quantity,position.EntryPrice);
                if (position.PartialExitPrice.HasValue) paper[LiveFillPnl.Leg.Partial]=new(position.PartialExitQuantity,position.PartialExitPrice.Value);
                if (position.ClosedAtUtc.HasValue) paper[LiveFillPnl.Leg.Final]=new(position.Quantity-position.PartialExitQuantity,position.CurrentPrice);
            }
            else
            {
                foreach (var (role,suffix) in PaperLegReferences)
                    if (LiveFillPnl.ReadPaperLeg(journal.GetValueOrDefault($"{source:N}-{suffix}")) is { } leg)
                        paper[role]=leg;
            }
            var intent=intents.GetValueOrDefault(source);
            return DailyExecutionEvidence.Review(t,intent,lifecycle[source],route,fillAt) with
            {
                StopHistory = intent is not null ? StopComparison.Review(source,protection[source]) : [],
                Pnl = LiveFillPnl.Calculate(intent,economics[source],paper)
            };
        }).ToArray();
        PaperSessionTimeline? timeline = null;
        if (withTimeline)
        {
            var eligible = trades.Where(t => t.Market is "NIFTY" or "SENSEX" or "BANKNIFTY" or "BANK-NIFTY").ToArray();
            var knownEntries = signalIds.Select(id => $"{id:N}-ENTRY").ToArray();
            var missing = await db.PaperBrokerEvents.AsNoTracking().AnyAsync(e => e.EventType=="OrderFilled" &&
                e.OccurredAtUtc>=startUtc && e.OccurredAtUtc<endUtc && e.ClientReference.EndsWith("-ENTRY") &&
                !knownEntries.Contains(e.ClientReference), cancellationToken);
            if (missing || eligible.Any(t => t.ClosedAtUtc is null))
                timeline = new([], "An entry has no completed trade record. A combined session curve cannot be verified.");
            else
            {
                var series = new List<PaperTimelineTrade>();
                var partials = audits.Where(a=>a.Outcome.StartsWith("PartialProfit:",StringComparison.Ordinal)).ToLookup(a=>a.Outcome);
                var entryReceipts=audits.Where(a=>a.Outcome=="PaperPositionOpened")
                    .SelectMany(a=>StopComparison.Parse(a.CandleTimeUtc,a.Outcome,a.ReasonsJson)).ToLookup(p=>p.Source);
                foreach (var t in eligible)
                {
                    var source = t.SignalId ?? t.TradeId;
                    var actions = new List<PaperTimelineEvent>();
                    bool valid = true;
                    var sign = 1;
                    if (positionMap.TryGetValue(t.TradeId,out var pos))
                    {
                        sign = pos.Direction == Direction.Buy ? 1 : -1;
                        var entryTime = entryReceipts[source].FirstOrDefault()?.At ?? t.OpenedAtUtc;
                        actions.Add(new(entryTime,"Entry",pos.Quantity,pos.EntryPrice));
                        foreach (var a in partials[$"PartialProfit:{source:N}"])
                        {
                            var ev = ReadPartial(a.CandleTimeUtc,a.ReasonsJson);
                            if(ev is null) valid=false; else actions.Add(ev);
                        }
                        valid &= actions.Where(a=>a.Kind=="Partial").Sum(a=>a.Units)==pos.PartialExitQuantity;
                        actions.Add(new(t.ClosedAtUtc!.Value,"Exit",pos.Quantity-pos.PartialExitQuantity,pos.CurrentPrice));
                    }
                    else
                    {
                        foreach (var (_,suffix) in PaperLegReferences)
                        {
                            var key=$"{source:N}-{suffix}";
                            var rows=fillRows.Where(x=>x.ClientReference==key).ToArray();
                            if(rows.Length==0) continue;
                            var row=rows.MaxBy(x=>x.Sequence)!;
                            var leg=LiveFillPnl.ReadPaperLeg(row.PayloadJson);
                            if(leg is null || rows.Length!=1) valid=false;
                            if(leg is not null) actions.Add(new(row.OccurredAtUtc,suffix switch{
                                "ENTRY"=>"Entry","ADDON"=>"Add-on","PARTIAL"=>"Partial",_=>"Exit"},leg.Qty,leg.Price));
                        }
                    }
                    var from=actions.FirstOrDefault(a=>a.Kind=="Entry")?.At ?? t.OpenedAtUtc;
                    var to=actions.FirstOrDefault(a=>a.Kind=="Exit")?.At ?? t.ClosedAtUtc!.Value;
                    if(to<from || from<startUtc.AddHours(9.25) || to>startUtc.AddHours(15.5)){
                        timeline=new([], "A trade falls outside the session clock; combined results are unavailable."); break;
                    }
                    var ordered=actions.OrderBy(a=>a.At).ToArray();
                    int held=0;
                    foreach(var e in ordered){
                        held+=e.Kind is "Entry" or "Add-on"?e.Units:-e.Units;
                        valid &= e.At>=from && e.At<=to && held>=0 && e.Units>0 && e.Price>0;
                    }
                    valid &= held==0 && actions.Any(a=>a.Kind=="Entry") && actions.Any(a=>a.Kind=="Exit") &&
                        actions.Where(a=>a.Kind is "Entry" or "Add-on").Sum(a=>a.Units)==t.Quantity &&
                        Math.Abs(sign*actions.Sum(a=>(a.Kind is "Entry" or "Add-on"?-1:1)*a.Price*a.Units)-t.GrossPnl)<=0.05m;
                    decimal? lastStop=null;
                    foreach(var p in protection[source].Where(p=>p.Type==StopComparison.Kind.Desired && p.Price>0 && p.At>=from && p.At<=to).OrderBy(p=>p.At)){
                        if(p.Price!=lastStop) actions.Add(new(p.At,"Stop",0,p.Price!.Value));
                        lastStop=p.Price;
                    }
                    series.Add(new(t.TradeId,from,to,t.GrossPnl,sign,valid,actions.OrderBy(a=>a.At).ToArray(),[]));
                }
                if(timeline is null){
                    var records=eligible.ToDictionary(t=>t.TradeId);
                    var windows=series.Select(t=>(t.Id,records[t.Id].Market=="SENSEX"?"BSE":"NSE",records[t.Id].Symbol,t.From,t.To)).ToArray();
                    try{
                        if(quoteArchive is null)timeline=new(series,null,"Quote archive unavailable. Only recorded fills are shown.");
                        else{
                            var (marks,gaps)=await quoteArchive.HeldMarksAsync(tradingDate,windows,cancellationToken);
                            timeline=new(series.Select(t=>t with { Marks=marks.GetValueOrDefault(t.Id)??[] }).ToArray(),
                                null,gaps?"The archive reports incomplete capture. Gaps are not interpolated.":null);
                        }
                    }catch(Exception e) when(e is IOException or UnauthorizedAccessException or InvalidDataException){
                        timeline=new(series,null,"Quote archive could not be read completely. Only recorded fills are shown.");
                    }
                }
            }
        }
        return new DailyTradingDiagnosticsReport(
            tradingDate,
            normalizedMarket,
            timeProvider.GetUtcNow(),
            summary,
            feeds,
            decisions.TakeLast(MaximumDecisions).ToArray(),
            trades,
            events,
            warnings)
        {
            EngineEvidenceCoverage = engineCoverage,
            Timeline = timeline,
            ExecutionReview = reviews,
            LivePnl = LiveFillPnl.Summarize(reviews.Select(r=>r.Pnl!)),
            ExpansionPairObservations = audits
                .Where(value => value.Outcome == "ExpansionPair:Observation")
                .Select(value => new ExpansionPairObservation(value.Market.ToUpperInvariant(),
                    value.CandleTimeUtc, value.ReasonsJson))
                .ToArray()
        };
    }

    private static PaperTimelineEvent? ReadPartial(DateTimeOffset at,string json)
    {
        try{
            using var doc=System.Text.Json.JsonDocument.Parse(json);
            var r=doc.RootElement;
            if(r.ValueKind==System.Text.Json.JsonValueKind.Object && r.TryGetProperty("partialQuantity",out var q) &&
                q.ValueKind==System.Text.Json.JsonValueKind.Number && q.TryGetInt32(out var units) && units>0 &&
                r.TryGetProperty("price",out var p) && p.ValueKind==System.Text.Json.JsonValueKind.Number &&
                p.TryGetDecimal(out var price) && price>0) return new(at,"Partial",units,price);
        }catch(System.Text.Json.JsonException){}
        return null;
    }

    private static readonly (LiveFillPnl.Leg Role,string Suffix)[] PaperLegReferences =
        [(LiveFillPnl.Leg.Entry,"ENTRY"),(LiveFillPnl.Leg.AddOn,"ADDON"),(LiveFillPnl.Leg.Partial,"PARTIAL"),(LiveFillPnl.Leg.Final,"EXIT")];

    internal sealed record JournalFillReceipt(string ClientReference, DateTimeOffset At);

    internal static IQueryable<JournalFillReceipt> JournalEntryFills(IQueryable<PaperBrokerEvent> events, string[] references) =>
        events.Where(value => references.Contains(value.ClientReference) && value.EventType == "OrderFilled")
            .GroupBy(value => value.ClientReference)
            .Select(group => new JournalFillReceipt(group.Key, group.Min(value => value.OccurredAtUtc)));

    internal static string NormalizeMarket(string market) => market.Trim().ToUpperInvariant() switch
    {
        "NIFTY" => "NIFTY",
        "SENSEX" => "SENSEX",
        "BANKNIFTY" or "BANK-NIFTY" => "BANKNIFTY",
        "BANKEX" => "BANKEX",
        "ALL" => "ALL",
        _ => throw new ArgumentOutOfRangeException(nameof(market), "Unsupported market.")
    };

    private static bool IsAccepted(string outcome) =>
        outcome.Contains("Opened", StringComparison.OrdinalIgnoreCase) ||
        outcome.Contains("Accepted", StringComparison.OrdinalIgnoreCase) ||
        outcome.Contains("Signal", StringComparison.OrdinalIgnoreCase) &&
        !outcome.Contains("NoSignal", StringComparison.OrdinalIgnoreCase);

    private static string MarketFromSymbol(string symbol) =>
        symbol.Contains("SENSEX", StringComparison.OrdinalIgnoreCase) ? "SENSEX" : "NIFTY";

    private static string Limit(string value) => value.Length <= 4000 ? value : value[..4000];

    internal static ProfitPath CalculateBoughtOptionExcursionPath(
        decimal entryPrice,
        int quantity,
        DateTimeOffset openedAtUtc,
        decimal exitPrice,
        DateTimeOffset closedAtUtc,
        IReadOnlyList<PaperTradePriceSample> samples)
    {
        var eligible = samples.Where(value => value.ObservedAtUtc >= openedAtUtc &&
            value.ObservedAtUtc <= closedAtUtc).ToArray();
        var points = eligible.Select(value => new PaperTradePricePoint(value.ObservedAtUtc, value.OptionPrice))
            .Append(new PaperTradePricePoint(closedAtUtc, exitPrice))
            .Append(new PaperTradePricePoint(openedAtUtc, entryPrice))
            .Where(value => value.Price > 0).ToArray();
        // Nifty's Sell signal buys a PE; the option price path is long, not short.
        return CalculateExcursionPath(entryPrice, quantity, Direction.Buy, points,
            eligible.Length > 0 ? "Sampled price path; historical cadence" : "Entry/exit only");
    }

    private static ProfitPath CalculatePositionPath(MarketPaperPosition position,
        PaperTradePricePoint[] samples, DateTimeOffset exitAtUtc)
    {
        var points = samples.Where(value => value.ObservedAtUtc >= position.OpenedAtUtc &&
                value.ObservedAtUtc <= exitAtUtc)
            .Append(new PaperTradePricePoint(position.OpenedAtUtc, position.EntryPrice))
            .Append(new PaperTradePricePoint(exitAtUtc, position.CurrentPrice))
            .ToArray();
        return CalculateExcursionPath(position.EntryPrice, position.Quantity, position.Direction,
            points, samples.Length > 0 ? "Historical sampled path; legacy cadence" : "Entry/exit only");
    }

    private static ProfitPath CalculateExcursionPath(decimal entryPrice, int quantity,
        Direction direction, PaperTradePricePoint[] points, string coverage)
    {
        var multiplier = direction == Direction.Buy ? 1m : -1m;
        var path = points.Where(value => value.Price > 0)
            .Select(value => (value.ObservedAtUtc, Pnl: (value.Price - entryPrice) * quantity * multiplier))
            .ToArray();
        var favourable = path.MaxBy(value => value.Pnl);
        var adverse = path.MinBy(value => value.Pnl);
        return new ProfitPath(Math.Max(0m, favourable.Pnl), favourable.ObservedAtUtc,
            Math.Max(0m, -adverse.Pnl), adverse.ObservedAtUtc, coverage, points.Length);
    }

    internal sealed record ProfitPath(
        decimal MaximumFavourableExcursion,
        DateTimeOffset MaximumFavourableExcursionAtUtc,
        decimal MaximumAdverseExcursion,
        DateTimeOffset MaximumAdverseExcursionAtUtc,
        string Coverage,
        int SampleCount);
}
