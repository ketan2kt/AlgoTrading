using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TradingSystem.Application.Execution;
using TradingSystem.Domain;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.Infrastructure.Execution;

internal sealed class EfTradingLotConfigurationService(
    TradingDbContext db, TimeProvider timeProvider) : ITradingLotConfigurationService
{
    internal const string Key = "IndexTradingLots";

    public async Task<TradingLotConfiguration> GetAsync(CancellationToken cancellationToken)
    {
        var setting = await db.ApplicationSettings.AsNoTracking().SingleOrDefaultAsync(value =>
            value.Mode == TradingMode.Paper && value.Key == Key, cancellationToken);
        if (setting is null) return TradingLotConfiguration.Default;
        try
        {
            var stored = JsonSerializer.Deserialize<StoredLots>(setting.ValueJson);
            return stored is not null && TradingLotConfiguration.IsValid(stored.NiftyLots) &&
                   TradingLotConfiguration.IsValid(stored.SensexLots) &&
                   TradingLotConfiguration.IsValid(stored.BankNiftyLots ?? TradingLotConfiguration.DefaultLots) &&
                   TradingLotConfiguration.IsValid(stored.BankexLots ?? TradingLotConfiguration.DefaultLots)
                ? new(stored.NiftyLots, stored.SensexLots, setting.UpdatedAtUtc,
                    stored.BankNiftyLots ?? TradingLotConfiguration.DefaultLots,
                    stored.BankexLots ?? TradingLotConfiguration.DefaultLots)
                : TradingLotConfiguration.Default;
        }
        catch (JsonException)
        {
            return TradingLotConfiguration.Default;
        }
    }

    public async Task<TradingLotConfiguration> SetAsync(int niftyLots, int sensexLots,
        string actor, CancellationToken cancellationToken)
    {
        var current = await GetAsync(cancellationToken);
        return await SetAsync(niftyLots, sensexLots, current.BankNiftyLots, current.BankexLots,
            actor, cancellationToken);
    }

    public async Task<TradingLotConfiguration> SetAsync(int niftyLots, int sensexLots,
        int bankNiftyLots, int bankexLots, string actor, CancellationToken cancellationToken)
    {
        if (!TradingLotConfiguration.IsValid(niftyLots) ||
            !TradingLotConfiguration.IsValid(sensexLots) ||
            !TradingLotConfiguration.IsValid(bankNiftyLots) ||
            !TradingLotConfiguration.IsValid(bankexLots))
            throw new ArgumentOutOfRangeException(nameof(niftyLots),
                $"Lots must be between {TradingLotConfiguration.MinimumLots} and {TradingLotConfiguration.MaximumLots}.");
        var now = timeProvider.GetUtcNow();
        var json = JsonSerializer.Serialize(new StoredLots(niftyLots, sensexLots,
            bankNiftyLots, bankexLots));
        var setting = await db.ApplicationSettings.SingleOrDefaultAsync(value =>
            value.Mode == TradingMode.Paper && value.Key == Key, cancellationToken);
        var before = setting?.ValueJson ?? "null";
        if (setting is null)
            db.ApplicationSettings.Add(new ApplicationSetting(Guid.NewGuid(), TradingMode.Paper,
                Key, json, now));
        else
            setting.ChangeValue(json, now);
        db.AuditLogs.Add(new AuditLog(Guid.NewGuid(), actor, "IndexTradingLotsChanged",
            nameof(ApplicationSetting), Key, "Administrator changed index trade lots.",
            before, json, Guid.NewGuid().ToString("N"), now));
        await db.SaveChangesAsync(cancellationToken);
        return new(niftyLots, sensexLots, now, bankNiftyLots, bankexLots);
    }

    private sealed record StoredLots(int NiftyLots, int SensexLots,
        int? BankNiftyLots = null, int? BankexLots = null);
}
