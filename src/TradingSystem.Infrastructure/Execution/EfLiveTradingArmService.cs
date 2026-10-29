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
    LiveExecutionWakeSignal wakeSignal,
    TimeProvider timeProvider) : ILiveTradingArmService
{
    internal const string KeyPrefix = "LiveExecutionArm:";

    public async Task<LiveTradingArmStatus> GetAsync(CancellationToken cancellationToken)
    {
        var settings = await db.ApplicationSettings.AsNoTracking().Where(value =>
            value.Mode == TradingMode.Live && value.Key.StartsWith(KeyPrefix))
            .ToListAsync(cancellationToken);
        var markets = options.Value.AllowedMarkets.Select(market =>
        {
            var value = Parse(settings.FirstOrDefault(item => item.Key == KeyPrefix + market)?.ValueJson);
            return new LiveMarketArmStatus(market, value?.Armed == true,
                value?.ChangedAtUtc, value?.ChangedBy);
        }).ToArray();
        var tested = await db.ApplicationSettings.AsNoTracking().AnyAsync(item =>
            item.Mode == TradingMode.Live && item.Key == AutomaticLiveExecutionService.ControlledTestKey &&
            item.ValueJson == "true", cancellationToken);
        var latestIntent = await db.LiveExecutionIntents.AsNoTracking()
            .OrderByDescending(item => item.CreatedAtUtc).FirstOrDefaultAsync(cancellationToken);
        return ToStatus(markets,
            tested, latestIntent);
    }

    public async Task<LiveTradingArmStatus> SetAsync(string market, bool armed, string actor,
        CancellationToken cancellationToken)
    {
        if (!options.Value.AllowedMarkets.Contains(market, StringComparer.Ordinal))
            throw new ArgumentException("Market is not enabled for live execution.", nameof(market));
        if (armed && !options.Value.BuildEnabled)
            throw new InvalidOperationException("Live execution is disabled by server configuration.");
        var now = timeProvider.GetUtcNow();
        var value = new ArmValue(armed, now, actor);
        var json = JsonSerializer.Serialize(value);
        var key = KeyPrefix + market;
        var setting = await db.ApplicationSettings.SingleOrDefaultAsync(item =>
            item.Mode == TradingMode.Live && item.Key == key, cancellationToken);
        var before = setting?.ValueJson;
        if (setting is null)
            db.ApplicationSettings.Add(new ApplicationSetting(Guid.NewGuid(), TradingMode.Live, key, json, now));
        else
            setting.ChangeValue(json, now);
        await db.SaveChangesAsync(cancellationToken);
        await auditWriter.WriteAsync(new AuditEntry(actor,
            armed ? "LiveExecutionArmed" : "LiveExecutionDisarmed",
            nameof(ApplicationSetting), key, $"{market} live switch {(armed ? "ON" : "OFF")}", before ?? "null", json,
            Guid.NewGuid().ToString("N"), now), cancellationToken);
        wakeSignal.Notify();
        return await GetAsync(cancellationToken);
    }

    private LiveTradingArmStatus ToStatus(IReadOnlyList<LiveMarketArmStatus> markets, bool tested, LiveExecutionIntent? latestIntent) => new(options.Value.BuildEnabled,
        markets, true, tested, options.Value.AllowedMarkets,
        latestIntent?.Market, latestIntent?.Status, latestIntent?.LastError,
        latestIntent?.CreatedAtUtc);

    private static ArmValue? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<ArmValue>(json); }
        catch (JsonException) { return null; }
    }

    private sealed record ArmValue(bool Armed, DateTimeOffset ChangedAtUtc, string ChangedBy);
}
