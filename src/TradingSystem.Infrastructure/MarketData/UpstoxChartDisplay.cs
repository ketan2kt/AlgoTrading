using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TradingSystem.Application.MarketData;

namespace TradingSystem.Infrastructure.MarketData;

// Presentation cache only: never writes trading candles, feed readiness, marks or orders.
internal sealed class WorkspaceChartCache(TimeProvider clock)
{
    private sealed record Quote(decimal Price, DateTimeOffset At);
    private sealed record Bars(DateOnly Date, IReadOnlyList<WorkspaceCandle> Candles);
    private readonly ConcurrentDictionary<string, Quote> quotes = new();
    private readonly ConcurrentDictionary<string, Bars> bars = new();
    internal static string? Code(string market) => market switch
    { "NIFTY" => "nifty", "SENSEX" => "sensex", "BANKNIFTY" => "bank-nifty", _ => null };

    internal void Observe(string market, decimal price, DateTimeOffset at)
    {
        var now = clock.GetUtcNow();
        if (Code(market) is not { } code || price <= 0 || at > now || now - at > TimeSpan.FromSeconds(30)) return;
        quotes.AddOrUpdate(code, new Quote(price, at), (_, old) => at > old.At ? new Quote(price, at) : old);
    }

    internal void Store(string code, DateOnly date, IReadOnlyList<WorkspaceCandle> candles)
    {
        if (candles.Count > 0) bars[code] = new(date, candles.ToArray());
    }

    internal TradingWorkspaceSnapshot Apply(TradingWorkspaceSnapshot snapshot)
    {
        var now = clock.GetUtcNow();
        var date = DateOnly.FromDateTime(now.ToOffset(TimeSpan.FromHours(5.5)).DateTime);
        // A fresh trading quote alone is not enough to replace a recovered chart:
        // wait until Groww also has a current candle. Never change trading freshness.
        if (snapshot.IsFresh && snapshot.Candles.Any(c => c.OpenTimeUtc <= now && now - c.OpenTimeUtc <= TimeSpan.FromSeconds(90))) return snapshot;
        if (snapshot.MarketCode is not { } code || !bars.TryGetValue(code, out var value) || value.Date != date) return snapshot;
        quotes.TryGetValue(code, out var quote);
        var candles = value.Candles.Where(c => c.OpenTimeUtc <= now).ToArray();
        if (candles.Length == 0) return snapshot;
        var quoteFresh = quote is not null && quote.At <= now && now - quote.At <= TimeSpan.FromSeconds(30);
        // Upstox timestamps identify the START of the minute, not when its
        // closing price became available. Measure publication lag from the end.
        // Quote freshness remains an independent, unchanged 30-second check.
        var latest = candles[^1];
        var candleEnd = latest.OpenTimeUtc.AddSeconds(latest.IntervalSeconds);
        var candlesFresh = latest.IntervalSeconds == 60 && now - candleEnd <= TimeSpan.FromSeconds(90);
        return snapshot with
        {
            Candles = candles, FuturesVolume = [],
            ChartDisplay = new("Upstox", quoteFresh && candlesFresh, quote?.At, quote?.Price,
                quoteFresh, candlesFresh, now, candleEnd)
        };
    }
}

// One server-side cache for every browser. Uses existing backup index snapshots;
// only three bounded intraday-candle reads per refresh, never another quote stream.
internal sealed class UpstoxChartDisplay(IHttpClientFactory clients, IConfiguration configuration,
    WorkspaceChartCache cache, MarketWorkspaceRefreshQueue refresh, TimeProvider clock, ILogger<UpstoxChartDisplay> logger) : BackgroundService
{
    private static readonly string[] Markets = ["NIFTY", "SENSEX", "BANKNIFTY"];
    private static readonly Action<ILogger, string, string, Exception?> Failed = LoggerMessage.Define<string, string>(
        LogLevel.Warning, new EventId(6757, "ChartBackupUnavailable"), "Display-only chart backup for {Market} unavailable ({FailureType}). Trading is unchanged.");

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        ReplayRecorderHosting.IsRecorder(configuration) || configuration["ReplayBackup:Host"] != "Website" ||
        string.IsNullOrWhiteSpace(configuration["ReplayBackup:UpstoxAnalyticsToken"])
            ? Task.CompletedTask
            : Task.WhenAll(Markets.Select(m => RunMarketAsync(m, stoppingToken)));

    private async Task RunMarketAsync(string market, CancellationToken token)
    {
        var code = WorkspaceChartCache.Code(market)!;
        using var client = clients.CreateClient(UpstoxReplayBackupCapture.ClientName);
        while (!token.IsCancellationRequested)
        {
            var delay = TimeSpan.FromSeconds(15);
            var local = clock.GetUtcNow().ToOffset(TimeSpan.FromHours(5.5));
            // Warm chart cache stays independent of trading health, including
            // recovery where quotes reconnect before primary candles catch up.
            if (local.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) &&
                local.TimeOfDay >= new TimeSpan(9, 15, 0) && local.TimeOfDay < new TimeSpan(15, 31, 0))
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get,
                        "https://api.upstox.com/v3/historical-candle/intraday/" +
                        Uri.EscapeDataString(UpstoxReplayBackupCapture.IndexKey(market)!) + "/minutes/1");
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", configuration["ReplayBackup:UpstoxAnalyticsToken"]);
                    using var response = await client.SendAsync(request, token);
                    if ((int)response.StatusCode == 429)
                        delay = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - clock.GetUtcNow()) ?? TimeSpan.FromMinutes(1);
                    response.EnsureSuccessStatusCode();
                    using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(token));
                    var date = DateOnly.FromDateTime(local.DateTime);
                    var parsed = UpstoxHistoricalCandleBackup.Parse(document.RootElement, date);
                    var now = clock.GetUtcNow();
                    var candles = parsed.Candles.Select(c =>
                    {
                        var at = new DateTimeOffset(DateTime.Parse(c.SourceTimestamp, CultureInfo.InvariantCulture), TimeSpan.FromHours(5.5));
                        return new WorkspaceCandle(at, 60, c.Open, c.High, c.Low, c.Close, c.Volume, at.AddMinutes(1) <= now);
                    }).OrderBy(c => c.OpenTimeUtc).ToArray();
                    if (candles.Select(c => c.OpenTimeUtc).Distinct().Count() != candles.Length)
                        throw new InvalidDataException("Duplicate display candle timestamps");
                    cache.Store(code, date, candles);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch (Exception error)
                {
                    if (delay < TimeSpan.FromSeconds(30)) delay = TimeSpan.FromSeconds(30);
                    Failed(logger, market, UpstoxReplayBackupCapture.FailureDescription(error), null);
                }
                refresh.Request(code);
            }
            if (delay < TimeSpan.FromSeconds(15)) delay = TimeSpan.FromSeconds(15);
            try { await Task.Delay(delay, clock, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        }
    }
}
