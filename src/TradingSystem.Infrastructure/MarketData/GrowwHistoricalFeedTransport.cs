using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using NATS.Client.Core;
using NATS.NKeys;
using TradingSystem.Application.Execution;
using TradingSystem.Infrastructure.Broker.Groww;

namespace TradingSystem.Infrastructure.MarketData;

internal interface IGrowwHistoricalFeedTransport
{
    Task ConsumeAsync(IReadOnlyList<HistoricalFeedInstrument> instruments,
        Action<HistoricalFeedInstrument, string, GrowwFeedPoint, DateTimeOffset> onPoint,
        Action onGap, Action<DateTimeOffset> onSubscribed, CancellationToken cancellationToken);
}

// Read-only topics from the official Groww SDK. The ephemeral socket JWT/NKey
// never enters files, reports, URLs, source control or application logs.
internal sealed class GrowwHistoricalFeedTransport(IHttpClientFactory clients,
    IGrowwAccessTokenProvider tokens, TimeProvider clock) : IGrowwHistoricalFeedTransport
{
    internal const string HttpClientName = "GrowwHistoricalFeedAuth";
    public async Task ConsumeAsync(IReadOnlyList<HistoricalFeedInstrument> instruments,
        Action<HistoricalFeedInstrument, string, GrowwFeedPoint, DateTimeOffset> onPoint,
        Action onGap, Action<DateTimeOffset> onSubscribed, CancellationToken cancellationToken)
    {
        if (instruments.Count is 0 or > 1000) throw new ArgumentException("Invalid Groww subscription count.", nameof(instruments));
        using var key = KeyPair.CreatePair(PrefixByte.User);
        var jwt = await AuthorizeAsync(key.GetPublicKey(), cancellationToken);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await using var connection = new NatsConnection(new NatsOpts
        {
            Url = "wss://socket-api.groww.in",
            AuthOpts = new NatsAuthOpts { Jwt = jwt, Seed = key.GetSeed() },
            Name = "sarthi-historical-research"
        });
        connection.ConnectionDisconnected += (_, _) => { onGap(); lifetime.Cancel(); return ValueTask.CompletedTask; };
        connection.MessageDropped += (_, _) => { onGap(); lifetime.Cancel(); return ValueTask.CompletedTask; };
        await connection.ConnectAsync();
        var subscriptions = new List<INatsSub<byte[]>>();
        var consumers = new List<Task>();
        try
        {
            foreach (var instrument in instruments)
            foreach (var kind in instrument.Type == TradingSystem.Domain.Trading.InstrumentType.Index
                         ? new[] { "index" } : new[] { "price", "depth" })
            {
                lifetime.Token.ThrowIfCancellationRequested();
                var subject = Subject(instrument, kind);
                var subscription = await connection.SubscribeCoreAsync(subject,
                    serializer: NatsRawSerializer<byte[]>.Default,
                    opts: new NatsSubOpts { ChannelOpts = new NatsSubChannelOpts { Capacity = 128, FullMode = BoundedChannelFullMode.DropOldest } },
                    cancellationToken: lifetime.Token);
                subscriptions.Add(subscription);
                consumers.Add(ConsumeSubscriptionAsync(subscription, instrument, kind, onPoint, onGap, lifetime));
            }
            onSubscribed(clock.GetUtcNow());
            await Task.WhenAll(consumers);
        }
        finally
        {
            await lifetime.CancelAsync();
            foreach (var subscription in subscriptions) await subscription.DisposeAsync();
            try { await Task.WhenAll(consumers); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception) when (consumers.Any(value => value.IsFaulted)) { /* Observe failed consumers before leaving the connection scope. */ }
        }
    }

    internal async Task<string> AuthorizeAsync(string publicKey, CancellationToken cancellationToken)
    {
        var access = await tokens.GetAccessTokenAsync(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/api/apex/v1/socket/token/create/")
        {
            Content = JsonContent.Create(new { socketKey = publicKey })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        request.Headers.Add("X-API-VERSION", "1.0");
        using var response = await clients.CreateClient(HttpClientName).SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var body = new byte[65537];
        var length = 0;
        while (length < body.Length)
        {
            var count = await stream.ReadAsync(body.AsMemory(length), cancellationToken);
            if (count == 0) break;
            length += count;
        }
        if (length > 65536) throw new InvalidDataException("Invalid socket authorization response size.");
        using var authorization = JsonDocument.Parse(body.AsMemory(0, length));
        var payload = authorization.RootElement.TryGetProperty("payload", out var envelope) ? envelope : authorization.RootElement;
        if (!payload.TryGetProperty("token", out var token) || token.GetString() is not { Length: > 0 } jwt)
            throw new InvalidDataException("Socket authorization did not return a valid credential.");
        return jwt;
    }

    internal static string Subject(HistoricalFeedInstrument instrument, string kind)
    {
        var exchange = instrument.Exchange == "NSE" ? "nse" : "bse";
        return kind switch
        {
            "index" => $"/ld/indices/{exchange}/price.{instrument.ExchangeToken}",
            "price" => $"/ld/fo/{exchange}/price_detailed.{instrument.ExchangeToken}",
            "depth" => $"/ld/fo/{exchange}/book.{instrument.ExchangeToken}",
            _ => throw new ArgumentException("Unsupported market-data feed topic.", nameof(kind))
        };
    }

    private async Task ConsumeSubscriptionAsync(INatsSub<byte[]> subscription,
        HistoricalFeedInstrument instrument, string kind,
        Action<HistoricalFeedInstrument, string, GrowwFeedPoint, DateTimeOffset> onPoint,
        Action onGap, CancellationTokenSource lifetime)
    {
        try
        {
            await foreach (var message in subscription.Msgs.ReadAllAsync(lifetime.Token))
            {
                var received = clock.GetUtcNow();
                if (message.Data is null) throw new InvalidDataException("Empty feed frame.");
                var point = GrowwHistoricalFeedCodec.Decode(message.Data, kind, received, instrument.Exchange, instrument.Segment);
                onPoint(instrument, kind, point, received);
            }
            if (!lifetime.IsCancellationRequested) throw new InvalidDataException("A feed subscription ended before the capture window closed.");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch
        {
            onGap();
            await lifetime.CancelAsync();
            throw;
        }
    }
}
