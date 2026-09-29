using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TradingSystem.Application.Execution;
using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.Infrastructure.Execution;

internal sealed class DailyResearchPipelineV2Service(IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider) : BackgroundService
{
    internal const string Outcome = "ResearchPipeline:v2";
    private static readonly TimeZoneInfo India = FindIndiaTimeZone();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RunIfDueAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15), timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken)) await RunIfDueAsync(stoppingToken);
    }

    internal async Task RunIfDueAsync(CancellationToken token)
    {
        var now = timeProvider.GetUtcNow();
        var local = TimeZoneInfo.ConvertTime(now, India);
        if (local.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ||
            TimeOnly.FromDateTime(local.DateTime) < new TimeOnly(15, 35)) return;
        var date = DateOnly.FromDateTime(local.Date);
        var start = ToUtc(date); var end = ToUtc(date.AddDays(1));
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        foreach (var market in new[] { "nifty", "sensex" })
        {
            if (await db.MarketStrategyAudits.AsNoTracking().AnyAsync(value =>
                    value.Market == market && value.Outcome == Outcome &&
                    value.CandleTimeUtc >= start && value.CandleTimeUtc < end, token)) continue;
            var rows = await db.MarketStrategyAudits.AsNoTracking().Where(value =>
                    value.Market == market && value.Outcome == "DecisionSnapshot:v2" &&
                    value.CandleTimeUtc >= start && value.CandleTimeUtc < end)
                .OrderBy(value => value.CandleTimeUtc)
                .Select(value => new { value.UnderlyingInstrumentId, value.ReasonsJson })
                .ToListAsync(token);
            var snapshots = rows.Select(value => TryRead(value.ReasonsJson))
                .Where(value => value is not null).Cast<DecisionFeatureSnapshotV2>().ToArray();
            if (snapshots.Length == 0) continue;
            var instrumentId = rows[0].UnderlyingInstrumentId;
            var interval = await db.Candles.AsNoTracking().Where(value =>
                    value.InstrumentId == instrumentId && value.OpenTimeUtc >= start &&
                    value.OpenTimeUtc < end)
                .Select(value => value.IntervalSeconds).FirstOrDefaultAsync(token);
            var candles = await db.Candles.AsNoTracking().Where(value =>
                    value.InstrumentId == instrumentId && value.IntervalSeconds == interval &&
                    value.OpenTimeUtc >= start && value.OpenTimeUtc < end)
                .OrderBy(value => value.OpenTimeUtc)
                .Select(value => new StrategyPriceBar(value.OpenTimeUtc, value.Open, value.High,
                    value.Low, value.Close)).ToListAsync(token);
            var evaluated = snapshots.Where(value => value.CandidateDirection is not null &&
                    value.ProposedStop is not null && value.ProposedTarget is not null)
                .Select(snapshot => new
                {
                    Snapshot = snapshot,
                    Result = CounterfactualOutcomeAnalyzerV2.Evaluate(
                        snapshot.CandidateDirection!.Value, snapshot.Price,
                        snapshot.ProposedStop!.Value, snapshot.ProposedTarget!.Value,
                        candles.Where(value => value.OpenTimeUtc > snapshot.CandleTimeUtc)
                            .Take(12).ToArray())
                }).ToArray();
            var calibration = ProbabilityCalibrationAnalyzerV2.Analyze(evaluated
                .Where(value => value.Result.Outcome is "TargetFirst" or "StopFirst")
                .Select(value => (value.Snapshot.CandidateConfidence,
                    value.Result.Outcome == "TargetFirst")));
            var walkForward = evaluated.GroupBy(value => value.Snapshot.Strategy)
                .Select(group => Validate(group.Key, group.OrderBy(value =>
                    value.Snapshot.CandleTimeUtc).Select(value => value.Result).ToArray()))
                .ToArray();
            var improvementJson = await db.MarketStrategyAudits.AsNoTracking().Where(value =>
                    value.Market == market &&
                    value.Outcome == SelfImprovementResearchService.AuditOutcome)
                .OrderByDescending(value => value.CandleTimeUtc)
                .Select(value => value.ReasonsJson).FirstOrDefaultAsync(token);
            var improvement = string.IsNullOrWhiteSpace(improvementJson) ? null :
                JsonSerializer.Deserialize<SelfImprovementResearchReport>(improvementJson);
            var report = new DailyResearchPipelineReportV2("research-pipeline-v2", market, date,
                snapshots.Length, evaluated.Length,
                evaluated.Count(value => value.Result.Outcome == "TargetFirst"),
                evaluated.Count(value => value.Result.Outcome == "StopFirst"),
                evaluated.Count(value => value.Result.Outcome == "Unresolved"),
                snapshots.GroupBy(value => value.State.State.ToString())
                    .ToDictionary(group => group.Key, group => group.Count()),
                calibration, walkForward, PaperPromotionPolicy.Evaluate(improvement), now);
            db.MarketStrategyAudits.Add(new(Guid.NewGuid(), market, instrumentId, now, Outcome,
                report.Promotion.Eligible ? 1m : 0m, JsonSerializer.Serialize(report)));
            await db.SaveChangesAsync(token);
        }
    }

    private static StrategyValidationV2 Validate(string strategy,
        CounterfactualOutcomeV2[] source)
    {
        var resolved = source.Where(value => value.Outcome is "TargetFirst" or "StopFirst").ToArray();
        var split = resolved.Length < 2 ? resolved.Length : Math.Clamp(
            (int)Math.Floor(resolved.Length * .70m), 1, resolved.Length - 1);
        var training = resolved.Take(split).ToArray(); var validation = resolved.Skip(split).ToArray();
        decimal Rate(CounterfactualOutcomeV2[] rows) => rows.Length == 0 ? 0m :
            (decimal)rows.Count(value => value.Outcome == "TargetFirst") / rows.Length;
        var status = validation.Length < 30 ? "CollectEvidence" :
            Rate(training) >= .80m && Rate(validation) >= .80m ? "Candidate" : "Rejected";
        return new(strategy, training.Length, Rate(training), validation.Length,
            Rate(validation), status);
    }

    private static DecisionFeatureSnapshotV2? TryRead(string json)
    {
        try { return JsonSerializer.Deserialize<DecisionFeatureSnapshotV2>(json); }
        catch (JsonException) { return null; }
    }

    private static DateTimeOffset ToUtc(DateOnly date)
    {
        var local = date.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, India.GetUtcOffset(local)).ToUniversalTime();
    }

    private static TimeZoneInfo FindIndiaTimeZone()
    {
        foreach (var id in new[] { "India Standard Time", "Asia/Kolkata" })
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
        return TimeZoneInfo.CreateCustomTimeZone("IST", TimeSpan.FromHours(5.5), "IST", "IST");
    }
}

internal sealed class EfDailyResearchPipelineV2Reader(TradingDbContext db)
    : IDailyResearchPipelineV2Reader
{
    public async Task<IReadOnlyList<DailyResearchPipelineReportV2>> GetLatestAsync(
        CancellationToken cancellationToken)
    {
        var rows = await db.MarketStrategyAudits.AsNoTracking().Where(value =>
                value.Outcome == DailyResearchPipelineV2Service.Outcome &&
                (value.Market == "nifty" || value.Market == "sensex"))
            .OrderByDescending(value => value.CandleTimeUtc).Take(30)
            .Select(value => new { value.Market, value.ReasonsJson }).ToListAsync(cancellationToken);
        return rows.GroupBy(value => value.Market).Select(group => group.First())
            .Select(value => JsonSerializer.Deserialize<DailyResearchPipelineReportV2>(value.ReasonsJson))
            .Where(value => value is not null).Cast<DailyResearchPipelineReportV2>().ToArray();
    }
}
