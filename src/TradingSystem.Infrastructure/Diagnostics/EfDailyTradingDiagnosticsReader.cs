using Microsoft.EntityFrameworkCore;
using TradingSystem.Application.Diagnostics;
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
            ? new[] { "NIFTY", "SENSEX" }
            : new[] { normalizedMarket };
        var startUtc = new DateTimeOffset(
            tradingDate.ToDateTime(TimeOnly.MinValue), IndiaOffset).ToUniversalTime();
        var endUtc = startUtc.AddDays(1);
        var warnings = new List<string>();

        var underlyings = await db.Instruments.AsNoTracking()
            .Where(value => selectedMarkets.Contains(value.TradingSymbol))
            .Select(value => new { value.Id, value.TradingSymbol })
            .ToListAsync(cancellationToken);
        if (underlyings.Count == 0)
            warnings.Add("No matching Nifty/Sensex underlying instrument was found.");

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
            .Where(value => selectedMarkets.Contains(value.Market) &&
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
            .Where(value => selectedMarkets.Contains(value.Market) &&
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
            var path = CalculateLongPath(row.Result.EntryPrice, row.Result.Quantity,
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
                path.MaximumAdverseExcursionAtUtc));
        }

        trades.AddRange(marketPositions.Select(value => new DailyTradingTrade(
            value.Market.ToUpperInvariant(),
            "MarketPaperPosition",
            value.Id,
            null,
            executionSymbols.GetValueOrDefault(value.ExecutionInstrumentId, value.ExecutionInstrumentId.ToString()),
            value.Strategy,
            value.Direction.ToString(),
            value.Quantity,
            value.EntryPrice,
            value.CurrentPrice,
            value.OpenedAtUtc,
            value.ClosedAtUtc,
            value.Status,
            value.Status == "Active" ? null : value.Status,
            value.RealisedPnl + value.CostsPaid,
            value.CostsPaid,
            value.RealisedPnl,
            null,
            null,
            null,
            null)));
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
        "ALL" => "ALL",
        _ => throw new ArgumentOutOfRangeException(nameof(market), "Market must be nifty, sensex or all.")
    };

    private static bool IsAccepted(string outcome) =>
        outcome.Contains("Opened", StringComparison.OrdinalIgnoreCase) ||
        outcome.Contains("Accepted", StringComparison.OrdinalIgnoreCase) ||
        outcome.Contains("Signal", StringComparison.OrdinalIgnoreCase) &&
        !outcome.Contains("NoSignal", StringComparison.OrdinalIgnoreCase);

    private static string MarketFromSymbol(string symbol) =>
        symbol.Contains("SENSEX", StringComparison.OrdinalIgnoreCase) ? "SENSEX" : "NIFTY";

    private static string Limit(string value) => value.Length <= 4000 ? value : value[..4000];

    private static ProfitPath CalculateLongPath(
        decimal entryPrice,
        int quantity,
        DateTimeOffset closedAtUtc,
        IReadOnlyList<PaperTradePriceSample> samples)
    {
        var eligible = samples.Where(value => value.ObservedAtUtc <= closedAtUtc).ToArray();
        if (eligible.Length == 0)
            return new ProfitPath(null, null, null, null);

        var favourable = eligible.MaxBy(value => value.OptionPrice)!;
        var adverse = eligible.MinBy(value => value.OptionPrice)!;
        return new ProfitPath(
            (favourable.OptionPrice - entryPrice) * quantity,
            favourable.ObservedAtUtc,
            (adverse.OptionPrice - entryPrice) * quantity,
            adverse.ObservedAtUtc);
    }

    private sealed record ProfitPath(
        decimal? MaximumFavourableExcursion,
        DateTimeOffset? MaximumFavourableExcursionAtUtc,
        decimal? MaximumAdverseExcursion,
        DateTimeOffset? MaximumAdverseExcursionAtUtc);
}
