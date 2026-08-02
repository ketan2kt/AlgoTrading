using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TradingSystem.Application.Risk;
using TradingSystem.Domain;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.Infrastructure.Execution;

internal sealed class EfPaperKillSwitchService(TradingDbContext dbContext, TimeProvider timeProvider)
    : IPaperKillSwitchService
{
    private const string Key = "EmergencyKillSwitch";

    public async Task<PaperKillSwitchStatus> GetAsync(CancellationToken cancellationToken)
    {
        var setting = await dbContext.ApplicationSettings.AsNoTracking().SingleOrDefaultAsync(
            value => value.Mode == TradingMode.Paper && value.Key == Key, cancellationToken);
        return new(IsActive(setting?.ValueJson), setting?.UpdatedAtUtc);
    }

    public async Task<PaperKillSwitchStatus> SetAsync(bool active, string reason, string actor,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("Reason is required.", nameof(reason));
        var now = timeProvider.GetUtcNow();
        var setting = await dbContext.ApplicationSettings.SingleOrDefaultAsync(
            value => value.Mode == TradingMode.Paper && value.Key == Key, cancellationToken);
        var json = JsonSerializer.Serialize(active);
        if (setting is null)
        {
            setting = new ApplicationSetting(Guid.NewGuid(), TradingMode.Paper, Key, json, now);
            dbContext.ApplicationSettings.Add(setting);
        }
        else setting.ChangeValue(json, now);
        dbContext.AuditLogs.Add(new AuditLog(Guid.NewGuid(), actor,
            active ? "PaperKillSwitchActivated" : "PaperKillSwitchCleared",
            nameof(ApplicationSetting), setting.Id.ToString("N"), reason.Trim(), "{}", json,
            Guid.NewGuid().ToString("N"), now));
        await dbContext.SaveChangesAsync(cancellationToken);
        return new(active, now);
    }

    private static bool IsActive(string? json) =>
        !string.IsNullOrWhiteSpace(json) && bool.TryParse(json, out var active) && active;
}
