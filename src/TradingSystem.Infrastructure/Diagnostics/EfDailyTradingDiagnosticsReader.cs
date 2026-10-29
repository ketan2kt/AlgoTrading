using Microsoft.EntityFrameworkCore;
using TradingSystem.Application.Diagnostics;
using TradingSystem.Application.Execution;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.Infrastructure.Diagnostics;

internal sealed class EfDailyTradingDiagnosticsReader(
    TradingDbContext db,
    TimeProvider timeProvider) : IDailyTradingDiagnosticsReader
{
    private static readonly TimeSpan IndiaOffset = TimeSpan.FromHours(5.5);
    private const int MaximumDecisions = 500;
    private const int MaximumEvents = 100;

    public async Task<DailyTradingDiagnosticsReport> ReadAsync(
        DateOnly tradingDate,
        string market,
        CancellationToken cancellationToken)
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
            .Concat(audits.Select(value => new DailyTradingDecision(
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
            samplesBySignal.TryGetValue(row.Result.SignalId, out var tradeSamples);
            var path = CalculateBoughtOptionExcursionPath(row.Result.EntryPrice, row.Result.Quantity,
                row.Signal.MarketDataTimestampUtc, row.Result.ExitPrice,
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
                row.Signal.MarketDataTimestampUtc,
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

        return new DailyTradingDiagnosticsReport(
            tradingDate,
            normalizedMarket,
            timeProvider.GetUtcNow(),
            summary,
            feeds,
            decisions.TakeLast(MaximumDecisions).ToArray(),
            trades,
            events,
            warnings);
    }

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
