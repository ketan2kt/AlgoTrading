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
    TimeProvider timeProvider,
    TradingSystem.Application.Broker.IUpstoxExecutionAuthorization? upstox = null) : ILiveTradingArmService
{
    internal const string KeyPrefix = "LiveExecutionArm:";

    public async Task<LiveTradingArmStatus> GetAsync(CancellationToken cancellationToken)
    {
        var settings = await db.ApplicationSettings.AsNoTracking().Where(value =>
            value.Mode == TradingMode.Live && value.Key.StartsWith(KeyPrefix))
            .ToListAsync(cancellationToken);
        var allowedMarkets = options.Value.AllowedMarkets
            .Where(market => market != "BANKEX").ToArray();
        var markets = allowedMarkets.Select(market =>
        {
            var value = Parse(settings.FirstOrDefault(item => item.Key == KeyPrefix + market)?.ValueJson);
            return new LiveMarketArmStatus(market, value?.Armed == true,
                value?.ChangedAtUtc, value?.ChangedBy, value?.ExecutionBroker ?? "Groww");
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
        if (market == "BANKEX" ||
            !options.Value.AllowedMarkets.Contains(market, StringComparer.Ordinal))
            throw new ArgumentException("Market is not enabled for live execution.", nameof(market));
        if (armed && !options.Value.BuildEnabled)
            throw new InvalidOperationException("Live execution is disabled by server configuration.");
        var now = timeProvider.GetUtcNow();
        var key = KeyPrefix + market;
        var setting = await db.ApplicationSettings.SingleOrDefaultAsync(item =>
            item.Mode == TradingMode.Live && item.Key == key, cancellationToken);
        var before = setting?.ValueJson;
        var selectedBroker = Parse(before)?.ExecutionBroker ?? "Groww";
        if (armed && selectedBroker == "Upstox")
        {
            var readiness = upstox is null ? null : await upstox.GetStatusAsync(cancellationToken);
            var segment = market == "SENSEX" ? "BFO" : "NFO";
            if (readiness is not { Configured: true, Authorized: true, StaticIpVerified: true, Sandbox: false } ||
                !readiness.Exchanges.Contains(segment, StringComparer.Ordinal))
                throw new InvalidOperationException("Upstox trading authorization, account segment or static-IP readiness is incomplete.");
        }
        var json = JsonSerializer.Serialize(new ArmValue(armed, now, actor, selectedBroker));
        if (setting is null)
            db.ApplicationSettings.Add(new ApplicationSetting(Guid.NewGuid(), TradingMode.Live, key, json, now));
        else
            setting.ChangeValue(json, now);
        // The scoped EF audit writer saves both tracked changes together. A switch
        // must not change successfully without its corresponding dated audit.
        await auditWriter.WriteAsync(new AuditEntry(actor,
            armed ? "LiveExecutionArmed" : "LiveExecutionDisarmed",
            nameof(ApplicationSetting), key, $"{market} live switch {(armed ? "ON" : "OFF")}", before ?? "null", json,
            Guid.NewGuid().ToString("N"), now), cancellationToken);
        wakeSignal.Notify();
        return await GetAsync(cancellationToken);
    }

    private LiveTradingArmStatus ToStatus(IReadOnlyList<LiveMarketArmStatus> markets, bool tested, LiveExecutionIntent? latestIntent) => new(options.Value.BuildEnabled,
        markets, true, tested,
        options.Value.AllowedMarkets.Where(market => market != "BANKEX").ToArray(),
        latestIntent?.Market, latestIntent?.Status, latestIntent?.LastError,
        latestIntent?.CreatedAtUtc);

    private static ArmValue? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<ArmValue>(json); }
        catch (JsonException) { return null; }
    }

    public async Task<LiveTradingArmStatus> SelectBrokerAsync(string market, string broker, string actor,
        CancellationToken cancellationToken)
    {
        if (broker is not ("Groww" or "Upstox") || market is not ("NIFTY" or "SENSEX" or "BANK-NIFTY") ||
            !options.Value.AllowedMarkets.Contains(market, StringComparer.Ordinal))
            throw new ArgumentException("Unsupported market or execution broker.");
        var key = KeyPrefix + market;
        var setting = await db.ApplicationSettings.SingleOrDefaultAsync(x => x.Mode == TradingMode.Live && x.Key == key,
            cancellationToken);
        var before = setting?.ValueJson;
        var current = Parse(before);
        if (current?.Armed == true)
            throw new InvalidOperationException("Turn new live entries OFF before changing execution broker. Existing trades stay managed.");
        var now = timeProvider.GetUtcNow();
        var json = JsonSerializer.Serialize(new ArmValue(false, now, actor, broker));
        if (setting is null) db.ApplicationSettings.Add(new(Guid.NewGuid(), TradingMode.Live, key, json, now));
        else setting.ChangeValue(json, now);
        await auditWriter.WriteAsync(new AuditEntry(actor, "LiveExecutionBrokerSelected", nameof(ApplicationSetting),
            key, $"{market} new entries routed to {broker}; existing positions unchanged.", before ?? "null", json,
            Guid.NewGuid().ToString("N"), now), cancellationToken);
        wakeSignal.Notify();
        return await GetAsync(cancellationToken);
    }

    private sealed record ArmValue(bool Armed, DateTimeOffset ChangedAtUtc, string ChangedBy,
        string ExecutionBroker = "Groww");
}
