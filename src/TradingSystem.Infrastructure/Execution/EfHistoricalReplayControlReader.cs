using System.Text.Json;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TradingSystem.Application.Execution;
using TradingSystem.Domain;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.Infrastructure.Execution;

internal sealed record HistoricalControlArchiveResult(IReadOnlyList<HistoricalReplayControlEvent> Events,
    bool InitialStateVerified, IReadOnlyList<string> Gaps);

// Read only administrative commands. Never use the original day's accepted or
// rejected strategy decisions to generate replay candidates.
internal sealed class EfHistoricalReplayControlReader(TradingDbContext db)
{
    public async Task<HistoricalControlArchiveResult> ReadAsync(string market, DateTimeOffset start,
        DateTimeOffset end, CancellationToken token)
    {
        var events = new List<HistoricalReplayControlEvent>();
        var gaps = new List<string>();
        if (market == "nifty")
        {
            var actions = new[] { "PaperKillSwitchActivated", "PaperKillSwitchCleared" };
            var prior = await db.AuditLogs.AsNoTracking().Where(value => actions.Contains(value.Action) &&
                    value.OccurredAtUtc < start).OrderByDescending(value => value.OccurredAtUtc)
                .FirstOrDefaultAsync(token);
            var setting = await db.ApplicationSettings.AsNoTracking().SingleOrDefaultAsync(value =>
                value.Mode == TradingMode.Paper && value.Key == "EmergencyKillSwitch", token);
            if (prior is not null) events.Add(Kill(prior));
            else if (setting is not null && setting.CreatedAtUtc < start)
            {
                if (setting.UpdatedAtUtc <= start && bool.TryParse(setting.ValueJson, out var active))
                    events.Add(new(setting.Id, setting.UpdatedAtUtc, "nifty", HistoricalReplayControlKind.KillSwitch, active));
                else gaps.Add("Nifty's initial kill-switch state cannot be proven from retained audit history.");
            }
            var changes = await db.AuditLogs.AsNoTracking().Where(value => actions.Contains(value.Action) &&
                value.OccurredAtUtc >= start && value.OccurredAtUtc <= end).ToArrayAsync(token);
            events.AddRange(changes.Select(Kill));
            var midnight = new DateTimeOffset(start.ToOffset(TimeSpan.FromHours(5.5)).Date, TimeSpan.FromHours(5.5)).ToUniversalTime();
            var overrides = await db.AuditLogs.AsNoTracking().Where(value =>
                (value.Action == "PaperDailyLossLimitOverridden" || value.Action == "PaperDailyLossLimitRestored") &&
                value.OccurredAtUtc >= midnight && value.OccurredAtUtc <= end).ToArrayAsync(token);
            foreach (var change in overrides)
            {
                try
                {
                    using var json = JsonDocument.Parse(change.AfterJson);
                    var active = json.RootElement.GetProperty("Active").GetBoolean();
                    var date = DateOnly.ParseExact(json.RootElement.GetProperty("SessionDate").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                    if (date != DateOnly.FromDateTime(start.ToOffset(TimeSpan.FromHours(5.5)).DateTime))
                        throw new InvalidDataException("A historical daily-risk command names another session.");
                    events.Add(new(change.Id, change.OccurredAtUtc, market,
                        HistoricalReplayControlKind.DailyLossOverride, active));
                }
                catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
                { gaps.Add("A historical daily-risk command is invalid."); }
            }
            var engineEvents = await db.SystemEvents.AsNoTracking().Where(value =>
                    value.EventType == "EngineEvidence.nifty.regular" && value.OccurredAtUtc >= start && value.OccurredAtUtc <= end)
                .Select(value => new { value.Id, value.OccurredAtUtc, value.DetailsJson }).ToArrayAsync(token);
            foreach (var item in engineEvents)
            {
                try
                {
                    if (ReconciliationBlocked(item.DetailsJson)) events.Add(new(item.Id, item.OccurredAtUtc,
                        "nifty", HistoricalReplayControlKind.ReconciliationHold, true));
                }
                catch (InvalidDataException) { gaps.Add("A retained engine-control observation cannot be verified."); }
            }
        }
        var requests = await db.MarketStrategyAudits.AsNoTracking().Where(value => value.Market == market &&
            value.CandleTimeUtc >= start && value.CandleTimeUtc <= end && value.Outcome.StartsWith("ManualExitRequested:"))
            .ToArrayAsync(token);
        foreach (var request in requests)
        {
            if (!Guid.TryParseExact(request.Outcome["ManualExitRequested:".Length..], "N", out var tradeId))
            { gaps.Add("A historical manual-exit identity is invalid."); continue; }
            var position = await db.MarketPaperPositions.AsNoTracking().SingleOrDefaultAsync(value =>
                value.Id == tradeId && value.Market == market, token);
            if (position is not null && (position.Strategy.StartsWith("Hero Zero|", StringComparison.Ordinal) ||
                                        position.Strategy.StartsWith("Expansion Pair|", StringComparison.Ordinal))) continue;
            // The actual command targeted a particular trade identity. A changed
            // strategy might hold a different trade in the same contract then.
            // Do not silently reinterpret that human command as a strategy exit.
            gaps.Add("Manual intervention targets an original trade identity; this date is excluded from automatic strategy comparison.");
        }
        return new(HistoricalReplayControls.Validate(events), gaps.Count == 0, gaps);
    }

    internal static bool ReconciliationBlocked(string? details)
    {
        try
        {
            using var json = JsonDocument.Parse(details ?? "null");
            return json.RootElement.GetProperty("status").GetString() == "ReconciliationRequired";
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        { throw new InvalidDataException("Invalid engine-control evidence.", exception); }
    }

    private static HistoricalReplayControlEvent Kill(AuditLog audit)
    {
        var active = audit.Action == "PaperKillSwitchActivated";
        if (!bool.TryParse(audit.AfterJson, out var stored) || stored != active)
            throw new InvalidDataException("A historical kill-switch command conflicts with its stored state.");
        return new(audit.Id, audit.OccurredAtUtc, "nifty", HistoricalReplayControlKind.KillSwitch, active);
    }
}
