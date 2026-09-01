using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TradingSystem.Application.Auditing;
using TradingSystem.Application.Risk;
using TradingSystem.Domain;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.Infrastructure.Execution;

internal sealed class EfLiveTradingArmService(
    TradingDbContext db,
    IAuditWriter auditWriter,
    IOptions<LiveExecutionOptions> options,
    TimeProvider timeProvider) : ILiveTradingArmService
{
    internal const string Key = "LiveExecutionArm";
    private static readonly TimeZoneInfo IndiaTimeZone = FindIndiaTimeZone();

    public async Task<LiveTradingArmStatus> GetAsync(CancellationToken cancellationToken)
    {
        var setting = await db.ApplicationSettings.AsNoTracking().SingleOrDefaultAsync(value =>
            value.Mode == TradingMode.Live && value.Key == Key, cancellationToken);
        var value = Parse(setting?.ValueJson);
        var today = IndiaDate(timeProvider.GetUtcNow());
        var tested = await db.ApplicationSettings.AsNoTracking().AnyAsync(item =>
            item.Mode == TradingMode.Live && item.Key == AutomaticLiveExecutionService.ControlledTestKey &&
            item.ValueJson == "true", cancellationToken);
        return ToStatus(value is { Armed: true } && value.TradingDate == today ? value : null, tested);
    }

    public async Task<LiveTradingArmStatus> SetAsync(bool armed, string reason, string actor,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 10)
            throw new ArgumentException("A specific activation reason of at least 10 characters is required.", nameof(reason));
        if (armed && !options.Value.BuildEnabled)
            throw new InvalidOperationException("Live execution is disabled by server configuration.");
        var now = timeProvider.GetUtcNow();
        var value = new ArmValue(armed, IndiaDate(now), now, actor);
        var json = JsonSerializer.Serialize(value);
        var setting = await db.ApplicationSettings.SingleOrDefaultAsync(item =>
            item.Mode == TradingMode.Live && item.Key == Key, cancellationToken);
        var before = setting?.ValueJson;
        if (setting is null)
            db.ApplicationSettings.Add(new ApplicationSetting(Guid.NewGuid(), TradingMode.Live, Key, json, now));
        else
            setting.ChangeValue(json, now);
        await db.SaveChangesAsync(cancellationToken);
        await auditWriter.WriteAsync(new AuditEntry(actor,
            armed ? "LiveExecutionArmed" : "LiveExecutionDisarmed",
            nameof(ApplicationSetting), Key, reason.Trim(), before ?? "null", json,
            Guid.NewGuid().ToString("N"), now), cancellationToken);
        var tested = await db.ApplicationSettings.AsNoTracking().AnyAsync(item =>
            item.Mode == TradingMode.Live && item.Key == AutomaticLiveExecutionService.ControlledTestKey &&
            item.ValueJson == "true", cancellationToken);
        return ToStatus(value, tested);
    }

    private LiveTradingArmStatus ToStatus(ArmValue? value, bool tested) => new(options.Value.BuildEnabled,
        value?.Armed == true, value?.TradingDate, options.Value.MaximumLotsPerOrder, tested,
        options.Value.AllowedMarkets, value?.ChangedAtUtc, value?.ChangedBy);

    private static ArmValue? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<ArmValue>(json); }
        catch (JsonException) { return null; }
    }

    private static DateOnly IndiaDate(DateTimeOffset utc) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, IndiaTimeZone).Date);

    private static TimeZoneInfo FindIndiaTimeZone()
    {
        foreach (var id in new[] { "Asia/Kolkata", "India Standard Time" })
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch (TimeZoneNotFoundException) { }
        throw new InvalidOperationException("Asia/Kolkata timezone is unavailable.");
    }

    private sealed record ArmValue(bool Armed, DateOnly TradingDate,
        DateTimeOffset ChangedAtUtc, string ChangedBy);
}
