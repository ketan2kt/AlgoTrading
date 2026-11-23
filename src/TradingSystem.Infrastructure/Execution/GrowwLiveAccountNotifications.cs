using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using NATS.NKeys;
using TradingSystem.Infrastructure.Broker.Groww;
using TradingSystem.Infrastructure.MarketData;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.Infrastructure.Execution;

// Order/position events are wake-up hints only. REST fills and protection reads
// remain authoritative. This never subscribes to or changes recorder market data.
internal sealed partial class GrowwLiveAccountNotifications(IServiceScopeFactory scopes,
    IHttpClientFactory clients, IGrowwAccessTokenProvider tokens, LiveExecutionWakeSignal wake,
    IOptions<LiveExecutionOptions> options, TimeProvider clock,
    ILogger<GrowwLiveAccountNotifications> logger) : BackgroundService
{
    private readonly object wakeGate = new();
    private DateTimeOffset lastAccountWake = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.BuildEnabled) return;
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await HasExposureAsync(stoppingToken))
                { await Task.Delay(TimeSpan.FromSeconds(5), clock, stoppingToken); continue; }
                await ConsumeAsync(stoppingToken);
                failures = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception)
            {
                // Never log socket JWTs, account subjects or response bodies.
                AccountNotificationsUnavailable(logger);
                wake.Notify();
                failures = Math.Min(failures + 1, 5);
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, 5 * (1 << failures))), clock, stoppingToken);
            }
        }
    }

    private async Task<bool> HasExposureAsync(CancellationToken token)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<TradingDbContext>().LiveExecutionIntents
            .AnyAsync(i => i.Status != "Closed" && i.Status != "Rejected", token);
    }

    private async Task ConsumeAsync(CancellationToken token)
    {
        using var key = KeyPair.CreatePair(PrefixByte.User);
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/api/apex/v1/socket/token/create")
        { Content = JsonContent.Create(new { socketKey = key.GetPublicKey() }) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetAccessTokenAsync(token));
        request.Headers.Add("X-API-VERSION", "1.0");
        using var response = await clients.CreateClient(GrowwHistoricalFeedTransport.HttpClientName)
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync(65536, token);
        var body = await response.Content.ReadAsByteArrayAsync(token);
        if (body.Length > 65536) throw new InvalidDataException("Account socket authorization response too large.");
        using var json = JsonDocument.Parse(body);
        var payload = json.RootElement.TryGetProperty("payload", out var envelope) ? envelope : json.RootElement;
        var jwt = payload.GetProperty("token").GetString();
        var subscription = payload.GetProperty("subscriptionId").GetString();
        if (string.IsNullOrWhiteSpace(jwt)) throw new InvalidDataException("Account socket credential missing.");
        var subjects = Subjects(subscription ?? "");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        await using var connection = new NatsConnection(new NatsOpts
        {
            Url = "wss://socket-api.groww.in", Name = "sarthi-live-account",
            AuthOpts = new NatsAuthOpts { Jwt = jwt, Seed = key.GetSeed() }
        });
        connection.ConnectionDisconnected += (_, _) => { wake.Notify(); lifetime.Cancel(); return ValueTask.CompletedTask; };
        connection.MessageDropped += (_, _) => { wake.Notify(); lifetime.Cancel(); return ValueTask.CompletedTask; };
        await using var orders = await connection.SubscribeCoreAsync<byte[]>(subjects[0], cancellationToken: lifetime.Token);
        await using var positions = await connection.SubscribeCoreAsync<byte[]>(subjects[1], cancellationToken: lifetime.Token);
        var readers = new[] { ReadAsync(orders, lifetime.Token), ReadAsync(positions, lifetime.Token) };
        try
        {
            wake.Notify(); // Reconcile all changes missed while disconnected.
            while (!lifetime.IsCancellationRequested)
            {
                var delay = Task.Delay(TimeSpan.FromSeconds(30), clock, lifetime.Token);
                var finished = await Task.WhenAny(readers[0], readers[1], delay);
                if (finished != delay) { await finished; throw new IOException("Account notification subscription ended."); }
                await delay;
                if (!await HasExposureAsync(lifetime.Token)) return;
            }
        }
        finally
        {
            await lifetime.CancelAsync();
            try { await Task.WhenAll(readers); } catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        }
        throw new IOException("Account notification connection ended; reconnect with backoff.");
    }

    private async Task ReadAsync(INatsSub<byte[]> sub, CancellationToken token)
    {
        await foreach (var _ in sub.Msgs.ReadAllAsync(token))
        {
            // Both subjects share one debounce, so a burst of order and position
            // updates cannot multiply account-wide REST polling.
            lock (wakeGate)
            {
                if (clock.GetUtcNow() - lastAccountWake < TimeSpan.FromSeconds(2)) continue;
                lastAccountWake = clock.GetUtcNow();
                wake.Notify();
            }
        }
    }

    internal static string[] Subjects(string id)
    {
        if (id.Length is < 1 or > 200 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw new InvalidDataException("Invalid account subscription identifier.");
        return ["stocks_fo/order/updates.apex." + id, "stocks_fo/position/updates.apex." + id];
    }

    [LoggerMessage(LogLevel.Warning, "Live account notification stream unavailable; REST reconciliation remains active.")]
    private static partial void AccountNotificationsUnavailable(ILogger logger);
}
