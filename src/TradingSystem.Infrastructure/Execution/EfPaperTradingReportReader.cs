using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using TradingSystem.Application.Execution;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.Infrastructure.Execution;

internal sealed class EfPaperTradingReportReader(TradingDbContext db, TimeProvider timeProvider)
    : IPaperTradingReportReader
{
    private static readonly TimeZoneInfo India = FindIndiaTimeZone();

    public async Task<PaperTradingReport> GetAsync(int days, CancellationToken cancellationToken)
    {
        days = Math.Clamp(days, 1, 90);
        var cutoff = timeProvider.GetUtcNow().AddDays(-days - 2);
        var baseRows = await (from result in db.PaperTradeResults.AsNoTracking()
            join signal in db.Signals.AsNoTracking() on result.SignalId equals signal.Id
            join version in db.StrategyVersions.AsNoTracking() on signal.StrategyVersionId equals version.Id
            join strategy in db.Strategies.AsNoTracking() on version.StrategyId equals strategy.Id
            join instrument in db.Instruments.AsNoTracking() on result.InstrumentId equals instrument.Id
            where result.ClosedAtUtc >= cutoff
            orderby result.ClosedAtUtc descending
            select new { Result = result, Signal = signal, Strategy = strategy.Code,
                instrument.ExpiryDate })
            .Take(500).ToListAsync(cancellationToken);
        var signalIds = baseRows.Select(x => x.Signal.Id).ToArray();
        var priceSamples = await db.PaperTradePriceSamples.AsNoTracking()
            .Where(value => signalIds.Contains(value.SignalId))
            .OrderBy(value => value.ObservedAtUtc)
            .Select(value => new { value.SignalId, value.OptionPrice })
            .ToListAsync(cancellationToken);
        var sampleLookup = priceSamples.GroupBy(value => value.SignalId)
            .ToDictionary(group => group.Key,
                group => (IReadOnlyList<decimal>)group.Select(value => value.OptionPrice).ToArray());
        var followUps = await db.PaperExitFollowUps.AsNoTracking()
            .Where(value => signalIds.Contains(value.SignalId))
            .Select(value => new { value.SignalId, value.IncrementalPnlAfterExit })
            .ToListAsync(cancellationToken);
        var followUpLookup = followUps.GroupBy(value => value.SignalId)
            .ToDictionary(group => group.Key,
                group => (IReadOnlyList<decimal>)group.Select(value => value.IncrementalPnlAfterExit).ToArray());
        var regimeRows = await db.StrategyEvaluations.AsNoTracking()
            .Where(x => x.SignalId != null && signalIds.Contains(x.SignalId.Value))
            .Select(x => new { SignalId = x.SignalId!.Value, x.Regime, x.RecordedAtUtc,
                x.ShadowStructureState, x.ShadowTrendQuality, x.ShadowWouldPermit })
            .ToListAsync(cancellationToken);
        var regimes = regimeRows.GroupBy(x => x.SignalId)
            .ToDictionary(group => group.Key,
                group => group.OrderByDescending(x => x.RecordedAtUtc).First());
        var rows = baseRows.Select(x => new PaperTradeHistoryItem(x.Result.SignalId, x.Result.TradingSymbol,
                x.Signal.Direction.ToString(), x.Result.Quantity, x.Result.EntryPrice, x.Result.ExitPrice,
                x.Result.RealisedPnl, x.Result.ExitReason, x.Signal.MarketDataTimestampUtc,
                x.Result.ClosedAtUtc, x.Strategy,
                regimes.TryGetValue(x.Signal.Id, out var context) ? context.Regime.ToString() : "Unknown",
                x.ExpiryDate is { } expiry ? Math.Max(0, expiry.DayNumber - DateOnly.FromDateTime(
                    TimeZoneInfo.ConvertTime(x.Signal.MarketDataTimestampUtc, India).Date).DayNumber) : -1,
                ParseCosts(x.Result.CostBreakdownJson, x.Result.EstimatedCosts),
                PaperTradeResearchAnalyzer.Analyze(new(x.Result.Quantity, x.Result.EntryPrice,
                    x.Result.ExitPrice, x.Result.GrossPnl, x.Result.EstimatedCosts,
                    x.Result.RealisedPnl, x.Result.ExitReason,
                    sampleLookup.GetValueOrDefault(x.Signal.Id, []),
                    followUpLookup.GetValueOrDefault(x.Signal.Id, []))),
                context?.ShadowStructureState, context?.ShadowTrendQuality,
                context?.ShadowWouldPermit))
            .ToList();
        var daily = rows.GroupBy(value => DateOnly.FromDateTime(
                TimeZoneInfo.ConvertTime(value.ExitTimeUtc, India).Date))
            .OrderByDescending(group => group.Key).Take(days)
            .Select(group => Summarise(group.Key, group.OrderBy(value => value.ExitTimeUtc)))
            .ToArray();
        var breakdown = rows.GroupBy(value => new
            {
                value.Strategy,
                value.Regime,
                TimeBucket = TimeBucket(value.SignalTimeUtc),
                value.DaysToExpiry
            })
            .OrderByDescending(group => group.Count())
            .Select(group => new StrategyPerformanceBreakdown(group.Key.Strategy, group.Key.Regime,
                group.Key.TimeBucket, group.Key.DaysToExpiry, group.Count(),
                group.Count(x => x.RealisedPnl > 0), group.Sum(x => x.RealisedPnl),
                (decimal)group.Count(x => x.RealisedPnl > 0) / group.Count(),
                group.Average(x => x.RealisedPnl), group.Average(x => x.RealisedPnl),
                PaperTradeResearchAnalyzer.ProfitFactor(group.Select(x => x.RealisedPnl))))
            .ToArray();
        var evaluations = await db.StrategyEvaluations.AsNoTracking()
            .Where(value => value.RecordedAtUtc >= cutoff)
            .OrderByDescending(value => value.RecordedAtUtc).Take(5000)
            .Select(value => new { value.Outcome, value.RegimeConfidence,
                value.RelativeFuturesVolume, value.FailedConditionsJson })
            .ToListAsync(cancellationToken);
        var funnel = evaluations.GroupBy(value => value.Outcome)
            .OrderByDescending(group => group.Count())
            .Select(group => new StrategyDecisionFunnel(group.Key, group.Count(),
                group.Average(value => value.RegimeConfidence),
                group.Average(value => value.RelativeFuturesVolume),
                group.SelectMany(value => ParseReasons(value.FailedConditionsJson))
                    .GroupBy(reason => reason).OrderByDescending(reasons => reasons.Count()).Take(3)
                    .Select(reasons => new DecisionReasonCount(reasons.Key, reasons.Count())).ToArray()))
            .ToArray();
        var research = SummariseResearch(rows);
        var recommendations = BuildRecommendations(rows, breakdown, research);
        var shadow = rows.Where(value => value.ShadowWouldPermit is not null &&
                                         value.ShadowStructureState is not null)
            .GroupBy(value => new { State = value.ShadowStructureState!,
                WouldPermit = value.ShadowWouldPermit!.Value })
            .OrderByDescending(group => group.Count())
            .Select(group => new ShadowStructurePerformance(group.Key.State,
                group.Key.WouldPermit, group.Count(), group.Count(value => value.RealisedPnl > 0),
                group.Sum(value => value.RealisedPnl), group.Average(value => value.RealisedPnl)))
            .ToArray();
        return new(daily, rows, breakdown, funnel, recommendations, research, shadow,
            timeProvider.GetUtcNow());
    }

    private static string TimeBucket(DateTimeOffset utc)
    {
        var time = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, India).DateTime);
        return time < new TimeOnly(11, 0) ? "09:15-11:00"
            : time < new TimeOnly(13, 0) ? "11:00-13:00" : "13:00-cutoff";
    }

    private static DailyPaperPerformance Summarise(DateOnly date,
        IEnumerable<PaperTradeHistoryItem> trades)
    {
        var values = trades.ToArray();
        var peak = 0m; var equity = 0m; var drawdown = 0m;
        foreach (var trade in values)
        {
            equity += trade.RealisedPnl;
            peak = Math.Max(peak, equity);
            drawdown = Math.Max(drawdown, peak - equity);
        }
        var wins = values.Count(value => value.RealisedPnl > 0);
        return new(date, values.Length, wins, values.Count(value => value.RealisedPnl <= 0),
            values.Sum(value => value.RealisedPnl),
            values.Where(value => value.RealisedPnl > 0).Sum(value => value.RealisedPnl),
            values.Where(value => value.RealisedPnl < 0).Sum(value => value.RealisedPnl),
            values.Length == 0 ? 0m : (decimal)wins / values.Length, drawdown);
    }

    private static PaperResearchSummary SummariseResearch(List<PaperTradeHistoryItem> rows)
    {
        if (rows.Count == 0) return new(0, 0m, 0m, 0m, 0m, 0m, 0, 0);
        return new(rows.Count, rows.Average(value => value.RealisedPnl),
            PaperTradeResearchAnalyzer.ProfitFactor(rows.Select(value => value.RealisedPnl)),
            rows.Average(value => value.ExitQuality.MaximumFavourableExcursion),
            rows.Average(value => value.ExitQuality.MaximumAdverseExcursion),
            rows.Average(value => value.ExitQuality.ProfitGiveback),
            rows.Count(value => value.ExitQuality.Assessment == "PotentialEarlyExit"),
            rows.Count(value => value.ExitQuality.Assessment == "MaterialProfitGiveback"));
    }

    private static List<ResearchRecommendation> BuildRecommendations(
        List<PaperTradeHistoryItem> rows,
        IReadOnlyList<StrategyPerformanceBreakdown> breakdown, PaperResearchSummary research)
    {
        var result = new List<ResearchRecommendation>();
        var eligible = rows.Count >= 20;
        if (!eligible)
            result.Add(new("INSUFFICIENT_SAMPLE", "Information",
                $"Only {rows.Count} closed trades are available; collect at least 20 before testing parameter changes.",
                rows.Count, false));
        if (research.EarlyExitCandidates >= 3)
            result.Add(new("REVIEW_EARLY_EXITS", "Review",
                $"{research.EarlyExitCandidates} exits had material favourable movement after exit. Replay a stricter confirmation rule.",
                research.EarlyExitCandidates, eligible));
        if (research.ProfitGivebackCandidates >= 3)
            result.Add(new("REVIEW_PROFIT_PROTECTION", "Review",
                $"{research.ProfitGivebackCandidates} trades surrendered at least half of their observed favourable excursion. Compare trailing variants in replay.",
                research.ProfitGivebackCandidates, eligible));
        foreach (var weak in breakdown.Where(value => value.Trades >= 5 && value.Expectancy < 0)
                     .OrderBy(value => value.Expectancy).Take(3))
            result.Add(new("WEAK_SEGMENT", "Review",
                $"{weak.Strategy} in {weak.Regime} during {weak.TimeBucket} has negative observed expectancy. Test disabling this segment out of sample.",
                weak.Trades, eligible));
        if (result.Count == 0)
            result.Add(new("NO_MATERIAL_FINDING", "Information",
                "No sample-supported change is identified yet. Continue collecting paper evidence.",
                rows.Count, false));
        return result;
    }

    private static string[] ParseReasons(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<string[]>(json) ?? []; }
        catch (JsonException) { return ["Unreadable legacy reason"]; }
    }

    private static PaperTradingCostBreakdown ParseCosts(string json, decimal total)
    {
        if (!string.IsNullOrWhiteSpace(json))
            try
            {
                var result = JsonSerializer.Deserialize<PaperTradingCostBreakdown>(json);
                if (result is not null && result.ScheduleVersion is not null) return result;
            }
            catch (JsonException) { }
        return new("legacy-estimate", 0m, 0m, 0m, 0m, 0m, 0m, 0m, total);
    }

    private static TimeZoneInfo FindIndiaTimeZone()
    {
        foreach (var id in new[] { "Asia/Kolkata", "India Standard Time" })
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch (TimeZoneNotFoundException) { }
        throw new TimeZoneNotFoundException("India timezone unavailable.");
    }
}
