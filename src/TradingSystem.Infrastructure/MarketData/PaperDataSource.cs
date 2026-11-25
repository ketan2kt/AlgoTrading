using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using TradingSystem.Application.Broker;
using TradingSystem.Domain;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.Infrastructure.MarketData;

// Persist the preferred candle stream independently for each market. Switching
// the stream never resets positions, risk state, or the evaluated-candle cursor.
internal sealed class PaperDataSource(IServiceScopeFactory scopes, TimeProvider clock) : IDisposable
{
    internal const string SettingKey = "PaperDataBackupSession";
    internal const string RoutingKey = "PaperDataRoutingSession";
    private readonly SemaphoreSlim gate = new(1, 1);
    private string session = "";
    private Dictionary<string, string> sources = new(StringComparer.Ordinal);
    private Dictionary<Guid, string> instrumentMarkets = [];
    private readonly ConcurrentDictionary<(string, string), DateTimeOffset> ready = new();
    internal void RecordReady(string market, string provider) => ready[(Canonical(market), provider)] = clock.GetUtcNow();
    internal bool IsReady(string market, string provider) => ready.TryGetValue((Canonical(market), provider), out var at) &&
        at <= clock.GetUtcNow() && clock.GetUtcNow() - at <= TimeSpan.FromSeconds(15);
    public string Source => GetSource("NIFTY");
    public string GetSource(string market) => Volatile.Read(ref sources).GetValueOrDefault(Canonical(market), "Groww");
    public string GetSource(Guid instrumentId) => Volatile.Read(ref instrumentMarkets).TryGetValue(instrumentId, out var market) ? GetSource(market) : "Groww";
    internal static string Canonical(string market) => market.Replace("-", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
    internal string Today => clock.GetUtcNow().ToOffset(TimeSpan.FromHours(5.5)).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    public async Task InitializeAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            if (session == Today) return;
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            var saved = await db.ApplicationSettings.AsNoTracking().SingleOrDefaultAsync(x =>
                x.Mode == TradingMode.Paper && x.Key == RoutingKey, token);
            var route = saved is null ? null : System.Text.Json.JsonSerializer.Deserialize<Routing>(saved.ValueJson);
            Volatile.Write(ref sources, route?.Session == Today ? route.Sources : new(StringComparer.Ordinal));
            var indexes = await db.Instruments.AsNoTracking().Where(x => x.Type == InstrumentType.Index &&
                (x.TradingSymbol == "NIFTY" || x.TradingSymbol == "BANKNIFTY" || x.TradingSymbol == "SENSEX"))
                .Select(x => new { x.Id, x.TradingSymbol }).ToArrayAsync(token);
            Volatile.Write(ref instrumentMarkets, indexes.ToDictionary(x => x.Id, x => x.TradingSymbol));
            session = Today;
        }
        finally { gate.Release(); }
    }

    public Task UseBackupAsync(CancellationToken token) => UseAsync("NIFTY", "Upstox", token);
    public Task UseBackupAsync(string market, CancellationToken token) => UseAsync(market, "Upstox", token);
    public Task UsePrimaryAsync(string market, CancellationToken token) => UseAsync(market, "Groww", token);
    private async Task UseAsync(string market, string source, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            market = Canonical(market);
            if (market is not ("NIFTY" or "BANKNIFTY" or "SENSEX")) throw new ArgumentException("Unsupported paper market.", nameof(market));
            if (session == Today && GetSource(market) == source) return;
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            var updated = session == Today ? new Dictionary<string, string>(sources, StringComparer.Ordinal) : new(StringComparer.Ordinal);
            updated[market] = source;
            await Save(RoutingKey, System.Text.Json.JsonSerializer.Serialize(new Routing(Today, updated)));
            await db.SaveChangesAsync(token); // Must be durable before any backup signal is exposed.
            session = Today;
            Volatile.Write(ref sources, updated);

            async Task Save(string key, string value)
            {
                var setting = await db.ApplicationSettings.SingleOrDefaultAsync(x => x.Mode == TradingMode.Paper && x.Key == key, token);
                if (setting is null) db.ApplicationSettings.Add(new(Guid.NewGuid(), TradingMode.Paper, key, value, clock.GetUtcNow()));
                else setting.ChangeValue(value, clock.GetUtcNow());
            }
        }
        finally { gate.Release(); }
    }

    internal static string Of(IGrowwReadOnlyGateway gateway, string market = "NIFTY") => gateway is PaperMarketDataGateway paper ? paper.GetSource(market) : "Groww";
    internal static string Of(IGrowwReadOnlyGateway gateway, Guid instrumentId) => gateway is PaperMarketDataGateway paper ? paper.GetSource(instrumentId) : "Groww";
    private sealed record Routing(string Session, Dictionary<string, string> Sources);
    public void Dispose() => gate.Dispose();
}
