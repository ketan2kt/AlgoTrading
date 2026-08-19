using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TradingSystem.Application.Risk;
using TradingSystem.Domain;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.Infrastructure.Execution;

internal sealed class EfPaperDailyLossOverrideService(
    TradingDbContext dbContext,
    TimeProvider timeProvider) : IPaperDailyLossOverrideService
{
    internal const string Key = "DailyLossLimitOverride";
    private static readonly TimeZoneInfo IndiaTimeZone = FindIndiaTimeZone();

    public async Task<PaperDailyLossOverrideStatus> GetAsync(CancellationToken cancellationToken)
    {
        var today = CurrentIndiaDate();
        var setting = await dbContext.ApplicationSettings.AsNoTracking().SingleOrDefaultAsync(
            value => value.Mode == TradingMode.Paper && value.Key == Key, cancellationToken);
        var value = Parse(setting?.ValueJson);
        return new(value is { Active: true } && value.SessionDate == today,
            today, setting?.UpdatedAtUtc);
    }

    public async Task<PaperDailyLossOverrideStatus> SetAsync(bool active, string reason, string actor,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("Reason is required.", nameof(reason));

        var now = timeProvider.GetUtcNow();
        var today = CurrentIndiaDate();
        var value = new StoredOverride(active, today);
        var json = JsonSerializer.Serialize(value);
        var setting = await dbContext.ApplicationSettings.SingleOrDefaultAsync(
            item => item.Mode == TradingMode.Paper && item.Key == Key, cancellationToken);
        if (setting is null)
        {
            setting = new ApplicationSetting(Guid.NewGuid(), TradingMode.Paper, Key, json, now);
            dbContext.ApplicationSettings.Add(setting);
        }
        else
        {
            setting.ChangeValue(json, now);
        }

        dbContext.AuditLogs.Add(new AuditLog(Guid.NewGuid(), actor,
            active ? "PaperDailyLossLimitOverridden" : "PaperDailyLossLimitRestored",
            nameof(ApplicationSetting), setting.Id.ToString("N"), reason.Trim(), "{}", json,
            Guid.NewGuid().ToString("N"), now));
        await dbContext.SaveChangesAsync(cancellationToken);
        return new(active, today, now);
    }

    internal static bool IsActiveForDate(string? json, DateOnly date)
    {
        var value = Parse(json);
        return value is { Active: true } && value.SessionDate == date;
    }

    private DateOnly CurrentIndiaDate() => DateOnly.FromDateTime(
        TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), IndiaTimeZone).Date);

    private static StoredOverride? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<StoredOverride>(json); }
        catch (JsonException) { return null; }
    }

    private static TimeZoneInfo FindIndiaTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"); }
        catch (TimeZoneNotFoundException)
        { return TimeZoneInfo.FindSystemTimeZoneById("India Standard Time"); }
    }

    private sealed record StoredOverride(bool Active, DateOnly SessionDate);
}
