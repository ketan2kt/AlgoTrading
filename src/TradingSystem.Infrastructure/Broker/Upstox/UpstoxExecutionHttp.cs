using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace TradingSystem.Infrastructure.Broker.Upstox;

internal sealed class UpstoxExecutionHttp(IHttpClientFactory clients, IServiceScopeFactory scopes,
    IOptions<UpstoxExecutionOptions> options, TimeProvider clock) : IDisposable
{
    internal const string ClientName = "UpstoxExecutionApi";
    private readonly SemaphoreSlim budget = new(1, 1);
    private readonly Queue<DateTimeOffset> writes = new();
    private DateTimeOffset retryAfter;
    private readonly object readLock = new();
    private readonly Dictionary<string, (DateTimeOffset At, JsonElement Data)> reads = new(StringComparer.Ordinal);
    private long mutationVersion;

    public void Dispose() => budget.Dispose();

    internal async Task<JsonElement> SendAsync(HttpMethod method, string path, object? body, CancellationToken token)
    {
        if (!options.Value.Enabled || options.Value.Sandbox || !options.Value.StaticIpVerified)
            throw new InvalidOperationException("Upstox execution is not configured with verified egress.");
        await using var scope = scopes.CreateAsyncScope();
        var credential = await scope.ServiceProvider.GetRequiredService<IUpstoxExecutionCredentialProvider>().ReadAsync(token);
        if (clock.GetUtcNow() < retryAfter) throw new InvalidOperationException("Upstox execution is backing off after a rate limit; no request retried.");
        long readVersion;
        if (method != HttpMethod.Get)
        {
            await ReserveWriteAsync(token);
            lock (readLock) { reads.Clear(); mutationVersion++; }
        }
        lock (readLock)
        {
            readVersion = mutationVersion;
            if (method == HttpMethod.Get && reads.TryGetValue(path, out var saved) &&
                clock.GetUtcNow() - saved.At < TimeSpan.FromSeconds(1)) return saved.Data;
        }
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.AccessToken);
        request.Headers.Accept.Add(new("application/json"));
        if (body is not null) request.Content = JsonContent.Create(body);
        // Named client MUST have no automatic retry handler. A timeout is an unknown mutation outcome.
        using var response = await clients.CreateClient(ClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode)
        {
            if ((int)response.StatusCode == 429)
                retryAfter = response.Headers.RetryAfter?.Date ?? clock.GetUtcNow().Add(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(60));
            throw new UpstoxExecutionException((int)response.StatusCode);
        }
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
        var root = document.RootElement;
        if (!root.TryGetProperty("status", out var status) || status.GetString() != "success" ||
            !root.TryGetProperty("data", out var data) || data.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            throw new InvalidDataException("Upstox execution response is incomplete; reconcile before another action.");
        var result = data.Clone();
        if (method == HttpMethod.Get)
        {
            lock (readLock)
            {
                // Reuse bulk evidence within a short polling burst, never across a
                // mutation. A pre-write read completing late cannot restore stale data.
                if (readVersion == mutationVersion)
                {
                    if (reads.Count >= 256) reads.Clear();
                    reads[path] = (clock.GetUtcNow(), result);
                }
            }
        }
        return result;
    }

    private async Task ReserveWriteAsync(CancellationToken token)
    {
        await budget.WaitAsync(token);
        try
        {
            var now = clock.GetUtcNow();
            while (writes.TryPeek(out var oldest) && now - oldest >= TimeSpan.FromMinutes(30)) writes.Dequeue();
            // Combined place/modify/cancel budget. Fail before sending rather than retrying a write.
            if (writes.Count >= 1800 || writes.Count(x => now - x < TimeSpan.FromMinutes(1)) >= 450 ||
                writes.Count(x => now - x < TimeSpan.FromSeconds(1)) >= 8)
                throw new InvalidOperationException("Upstox execution request budget reached; defer new actions.");
            writes.Enqueue(now);
        }
        finally { budget.Release(); }
    }
}

internal sealed class UpstoxExecutionException(int status) : HttpRequestException($"Upstox execution returned HTTP {status}; response content withheld.")
{
    public int HttpStatus { get; } = status;
}
