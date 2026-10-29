using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingSystem.Application.Diagnostics;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Broker.Groww;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.Infrastructure.Diagnostics;

public sealed class OperationalAutomationOptions
{
    public const string SectionName = "OperationalAutomation";
    public bool Enabled { get; init; } = true;
}

internal sealed class OperationalAutomationService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    IOptions<OperationalAutomationOptions> options,
    ILogger<OperationalAutomationService> logger) : BackgroundService
{
    internal const string OutcomePrefix = "OperationalAutomation:v1:";
    private static readonly TimeZoneInfo India = FindIndiaTimeZone();
    private static readonly Action<ILogger, string, Exception?> LogSlotFailure =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(1, "OperationalSlotFailure"),
            "Operational automation slot {Slot} failed.");
    private static readonly Action<ILogger, string, Exception?> LogMissingUnderlying =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(2, "OperationalUnderlyingMissing"),
            "Operational automation could not find {Market} underlying.");
    private static readonly Slot[] Slots =
    [
        new("Readiness", new TimeOnly(9, 5)),
        new("NoTradeWatch:1030", new TimeOnly(10, 30)),
        new("NoTradeWatch:1200", new TimeOnly(12, 0)),
        new("NoTradeWatch:1400", new TimeOnly(14, 0)),
        new("DailyReport", new TimeOnly(16, 0)),
        new("TelemetryAudit", new TimeOnly(16, 5))
    ];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        await RunIfDueAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await RunIfDueAsync(stoppingToken);
    }

    internal async Task RunIfDueAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var local = TimeZoneInfo.ConvertTime(now, India);
        if (local.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return;
        var tradingDate = DateOnly.FromDateTime(local.DateTime);
        var localTime = TimeOnly.FromDateTime(local.DateTime);

        foreach (var slot in Slots.Where(value => localTime >= value.At))
        {
            try
            {
                await RunSlotAsync(slot, tradingDate, now, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                LogSlotFailure(logger, slot.Name, exception);
            }
        }
    }

    private async Task RunSlotAsync(Slot slot, DateOnly tradingDate,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var diagnostics = scope.ServiceProvider.GetRequiredService<IDailyTradingDiagnosticsReader>();
        var outcome = OutcomePrefix + slot.Name;
        var startUtc = ToUtc(tradingDate);
        var endUtc = ToUtc(tradingDate.AddDays(1));
        var tokenReady = true;
        string? tokenFailure = null;
        if (slot.Name == "Readiness")
        {
            try
            {
                _ = await scope.ServiceProvider.GetRequiredService<IGrowwAccessTokenProvider>()
                    .GetAccessTokenAsync(cancellationToken);
            }
            catch (Exception exception)
            {
                tokenReady = false;
                tokenFailure = exception.GetType().Name;
            }
        }

        foreach (var market in new[] { "nifty", "sensex", "bank-nifty", "bankex" })
        {
            if (await db.MarketStrategyAudits.AsNoTracking().AnyAsync(value =>
                    value.Market == market && value.Outcome == outcome &&
                    value.CandleTimeUtc >= startUtc && value.CandleTimeUtc < endUtc,
                    cancellationToken)) continue;

            var underlyingSymbol = market switch
            {
                "nifty" => "NIFTY",
                "sensex" => "SENSEX",
                "bank-nifty" => "BANKNIFTY",
                _ => "BANKEX"
            };
            var instrumentId = await db.Instruments.AsNoTracking()
                .Where(value => value.TradingSymbol == underlyingSymbol)
                .Select(value => (Guid?)value.Id)
                .FirstOrDefaultAsync(cancellationToken);
            if (instrumentId is null)
            {
                LogMissingUnderlying(logger, market, null);
                continue;
            }

            var source = await diagnostics.ReadAsync(tradingDate, market, cancellationToken);
            var report = BuildReport(slot, source, tokenReady, tokenFailure, now);
            db.MarketStrategyAudits.Add(new MarketStrategyAudit(
                Guid.NewGuid(), market, instrumentId.Value, now, outcome,
                report.Status == "Healthy" || report.Status == "Ready" ? 1m : .5m,
                JsonSerializer.Serialize(report)));
            if (report.ActionRequired)
            {
                db.SystemEvents.Add(new SystemEvent(Guid.NewGuid(), "Warning",
                    $"OperationalAutomation.{slot.Name}",
                    $"{market.ToUpperInvariant()}: {report.Headline}", now));
            }
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    internal static OperationalAutomationReport BuildReport(
        Slot slot,
        DailyTradingDiagnosticsReport source,
        bool tokenReady,
        string? tokenFailure,
        DateTimeOffset now)
    {
        var findings = new List<string>();
        var metrics = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["decisions"] = source.Summary.DecisionCount.ToString(CultureInfo.InvariantCulture),
            ["completedTrades"] = source.Summary.CompletedTradeCount.ToString(CultureInfo.InvariantCulture),
            ["openTrades"] = source.Summary.OpenTradeCount.ToString(CultureInfo.InvariantCulture),
            ["wins"] = source.Summary.Wins.ToString(CultureInfo.InvariantCulture),
            ["losses"] = source.Summary.Losses.ToString(CultureInfo.InvariantCulture),
            ["grossPnl"] = source.Summary.GrossPnl.ToString("0.00", CultureInfo.InvariantCulture),
            ["costs"] = source.Summary.Costs.ToString("0.00", CultureInfo.InvariantCulture),
            ["netPnl"] = source.Summary.NetRealisedPnl.ToString("0.00", CultureInfo.InvariantCulture),
            ["lastCandleUtc"] = source.Summary.LastCandleAtUtc?.ToString("O") ?? "unavailable"
        };

        string status;
        string headline;
        bool actionRequired;
        if (slot.Name == "Readiness")
        {
            var unavailableFeeds = source.Feeds.Where(value => !value.IsAvailable)
                .Select(value => value.Provider).ToArray();
            metrics["growwToken"] = tokenReady ? "ready" : "unavailable";
            metrics["unavailableFeeds"] = unavailableFeeds.Length.ToString(CultureInfo.InvariantCulture);
            if (!tokenReady) findings.Add($"Groww authentication failed ({tokenFailure ?? "unknown"}).");
            if (unavailableFeeds.Length > 0)
                findings.Add("Unavailable providers: " + string.Join(", ", unavailableFeeds));
            status = tokenReady && unavailableFeeds.Length == 0 ? "Ready" : "Attention";
            headline = status == "Ready"
                ? "Authentication and recorded providers are ready."
                : "Pre-market readiness requires attention.";
            actionRequired = status != "Ready";
        }
        else if (slot.Name.StartsWith("NoTradeWatch", StringComparison.Ordinal))
        {
            var tradeCount = source.Summary.CompletedTradeCount + source.Summary.OpenTradeCount;
            var dominant = source.Summary.Outcomes
                .Where(value => !value.Key.StartsWith(OutcomePrefix, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(value => value.Value).FirstOrDefault();
            metrics["dominantOutcome"] = dominant.Key ?? "unavailable";
            metrics["dominantOutcomeCount"] = dominant.Value.ToString(CultureInfo.InvariantCulture);
            if (source.Summary.DecisionCount == 0)
            {
                status = "Critical";
                headline = "No strategy decisions have been recorded.";
                actionRequired = true;
            }
            else if (tradeCount == 0)
            {
                status = slot.Name.EndsWith("1030", StringComparison.Ordinal) ? "Observe" : "Attention";
                headline = $"No trade yet; dominant recorded outcome is {dominant.Key ?? "unavailable"}.";
                actionRequired = status == "Attention";
            }
            else
            {
                status = "Healthy";
                headline = $"{tradeCount} trade(s) recorded; no-trade escalation is not active.";
                actionRequired = false;
            }
        }
        else if (slot.Name == "DailyReport")
        {
            status = source.Summary.DecisionCount > 0 ? "Healthy" : "Attention";
            headline = $"{source.Summary.CompletedTradeCount} completed trade(s), " +
                       $"{source.Summary.Wins} win(s), {source.Summary.Losses} loss(es), " +
                       $"net {source.Summary.NetRealisedPnl:0.00}.";
            actionRequired = source.Summary.DecisionCount == 0;
            findings.AddRange(source.TelemetryWarnings);
        }
        else
        {
            var completed = source.Trades.Where(value => value.ClosedAtUtc is not null).ToArray();
            var missingPath = completed.Count(value => value.MaximumFavourableExcursion is null);
            var unlinked = completed.Count(value => value.SignalId is null);
            metrics["missingProfitPaths"] = missingPath.ToString(CultureInfo.InvariantCulture);
            metrics["unlinkedTrades"] = unlinked.ToString(CultureInfo.InvariantCulture);
            if (missingPath > 0) findings.Add($"{missingPath} completed trade(s) lack an MFE/MAE path.");
            if (unlinked > 0) findings.Add($"{unlinked} completed trade(s) lack a signal/research link.");
            findings.AddRange(source.TelemetryWarnings.Where(value =>
                !value.StartsWith("No paper trade", StringComparison.OrdinalIgnoreCase)));
            actionRequired = missingPath > 0 || unlinked > 0 || source.Summary.DecisionCount == 0 ||
                             source.Summary.LastCandleAtUtc is null;
            status = actionRequired ? "Attention" : "Healthy";
            headline = actionRequired
                ? "The post-market telemetry audit found incomplete evidence."
                : "Trade, decision and profit-path telemetry is complete.";
        }

        return new OperationalAutomationReport(
            "operational-automation-v1", source.Market.ToLowerInvariant(), source.TradingDate,
            slot.Name, status, headline, metrics, findings.Distinct().ToArray(), actionRequired, now);
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

    internal sealed record Slot(string Name, TimeOnly At);
}

internal sealed class EfOperationalAutomationReportReader(TradingDbContext db)
    : IOperationalAutomationReportReader
{
    private static readonly TimeSpan IndiaOffset = TimeSpan.FromHours(5.5);

    public async Task<IReadOnlyList<OperationalAutomationReport>> GetLatestAsync(
        DateOnly? tradingDate,
        string market,
        CancellationToken cancellationToken)
    {
        var normalized = market.Trim().ToLowerInvariant();
        if (normalized is not ("all" or "nifty" or "sensex"))
            throw new ArgumentOutOfRangeException(nameof(market), "Market must be nifty, sensex or all.");
        var query = db.MarketStrategyAudits.AsNoTracking()
            .Where(value => value.Outcome.StartsWith(OperationalAutomationService.OutcomePrefix));
        if (normalized != "all") query = query.Where(value => value.Market == normalized);
        if (tradingDate is not null)
        {
            var start = new DateTimeOffset(tradingDate.Value.ToDateTime(TimeOnly.MinValue), IndiaOffset)
                .ToUniversalTime();
            var end = start.AddDays(1);
            query = query.Where(value => value.CandleTimeUtc >= start && value.CandleTimeUtc < end);
        }
        var json = await query.OrderByDescending(value => value.CandleTimeUtc).Take(40)
            .Select(value => value.ReasonsJson).ToListAsync(cancellationToken);
        return json.Select(TryRead).Where(value => value is not null)
            .Cast<OperationalAutomationReport>()
            .GroupBy(value => new { value.Market, value.Kind })
            .Select(value => value.First()).OrderBy(value => value.Market)
            .ThenBy(value => value.GeneratedAtUtc).ToArray();
    }

    private static OperationalAutomationReport? TryRead(string json)
    {
        try { return JsonSerializer.Deserialize<OperationalAutomationReport>(json); }
        catch (JsonException) { return null; }
    }
}
