using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NATS.NKeys;
using TradingSystem.Application.Execution;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Broker.Groww;

namespace TradingSystem.Infrastructure.MarketData;

internal interface IGrowwHistoricalFeedTransport
{
    Task ConsumeAsync(IReadOnlyList<HistoricalFeedInstrument> instruments,
        Action<HistoricalFeedInstrument, string, GrowwFeedPoint, DateTimeOffset> onPoint,
        Action onGap, Action<DateTimeOffset> onSubscribed,
        Action<HistoricalFeedInstrument, string, DateTimeOffset> onInvalidPoint, CancellationToken cancellationToken);
}

// Read-only topics from the official Groww SDK. The ephemeral socket JWT/NKey
// never enters files, reports, URLs, source control or application logs.
internal sealed class GrowwHistoricalFeedTransport(IHttpClientFactory clients,
    IGrowwAccessTokenProvider tokens, TimeProvider clock, ILogger<GrowwHistoricalFeedTransport>? logger = null) : IGrowwHistoricalFeedTransport
{
    internal const string HttpClientName = "GrowwHistoricalFeedAuth";
    internal const int MaximumSubjects = 1000;
    private static readonly string[] IndexChannels = ["index"];
    private static readonly string[] DerivativeChannels = ["price", "depth"];
    private static readonly Action<ILogger, string, string, Exception?> RenewingOptionChannel = LoggerMessage.Define<string, string>(
        LogLevel.Information, new EventId(6758, "ReplayOptionChannelRenewal"),
        "Renewing {Market} option {Channel} subscriptions on the existing recorder connection.");
    internal static int SubscriptionCount(IReadOnlyList<HistoricalFeedInstrument> instruments)
    {
        var count = instruments.Sum(value => value.Type == TradingSystem.Domain.Trading.InstrumentType.Index ? 1 : 2);
        if (instruments.Count == 0 || count > MaximumSubjects)
            throw new ArgumentException("Replay price/depth subscription budget exceeds 1000 subjects.", nameof(instruments));
        return count;
    }
    public async Task ConsumeAsync(IReadOnlyList<HistoricalFeedInstrument> instruments,
        Action<HistoricalFeedInstrument, string, GrowwFeedPoint, DateTimeOffset> onPoint,
        Action onGap, Action<DateTimeOffset> onSubscribed,
        Action<HistoricalFeedInstrument, string, DateTimeOffset> onInvalidPoint, CancellationToken cancellationToken)
    {
        _ = SubscriptionCount(instruments); // Fail before auth or any partial subscription.
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
        var groups = instruments.SelectMany(instrument => (instrument.Type == InstrumentType.Index
                ? IndexChannels : DerivativeChannels).Select(kind => (Instrument: instrument, Kind: kind)))
            .GroupBy(topic => (Market: topic.Instrument.Market, topic.Kind,
                Option: topic.Instrument.Type is InstrumentType.CallOption or InstrumentType.PutOption))
            .Select(group => new SubscriptionGroup(group.Key.Kind, group.Key.Option,
                group.Select(topic => topic.Instrument).ToArray())).ToArray();
        try
        {
            foreach (var group in groups) await OpenAsync(group);
            var subscribed = clock.GetUtcNow();
            onSubscribed(subscribed);
            using var tick = new PeriodicTimer(TimeSpan.FromSeconds(1), clock);
            while (await tick.WaitForNextTickAsync(lifetime.Token))
            {
                foreach (var group in groups)
                {
                    var now = clock.GetUtcNow();
                    if (group.Recovery?.TryStartRenewal(subscribed, now) != true) continue;
                    if (logger is not null) RenewingOptionChannel(logger, group.Instruments[0].Market, group.Kind, null);
                    // Release old subjects first: never use a second connection or
                    // temporarily exceed the original plan's subscription budget.
                    await group.CloseAsync();
                    await OpenAsync(group);
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally
        {
            await lifetime.CancelAsync();
            await Task.WhenAll(groups.Select(group => group.CloseAsync()));
        }

        async Task OpenAsync(SubscriptionGroup group)
        {
            var groupStop = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            group.Stop = groupStop;
            foreach (var instrument in group.Instruments)
            {
                groupStop.Token.ThrowIfCancellationRequested();
                var subscription = await connection.SubscribeCoreAsync(Subject(instrument, group.Kind),
                    serializer: NatsRawSerializer<byte[]>.Default,
                    opts: new NatsSubOpts { ChannelOpts = new NatsSubChannelOpts { Capacity = 128, FullMode = BoundedChannelFullMode.DropOldest } },
                    cancellationToken: groupStop.Token);
                group.Subscriptions.Add(subscription);
                group.Consumers.Add(ConsumeSubscriptionAsync(subscription, instrument, group.Kind, OnPoint,
                    onGap, onInvalidPoint, lifetime, groupStop.Token));
            }

            void OnPoint(HistoricalFeedInstrument instrument, string kind, GrowwFeedPoint point, DateTimeOffset received)
            {
                if (kind == "depth" ? point.Bid is > 0m && point.Offer >= point.Bid &&
                    point.BidQuantity > 0 && point.OfferQuantity > 0 : point.Last is > 0m)
                    group.Recovery?.Observe(point.SourceAtUtc, received);
                onPoint(instrument, kind, point, received);
            }
        }
    }

    private sealed class SubscriptionGroup(string kind, bool option, HistoricalFeedInstrument[] instruments)
    {
        public string Kind { get; } = kind;
        public HistoricalFeedInstrument[] Instruments { get; } = instruments;
        public GrowwOptionFeedRecovery? Recovery { get; } = option ? new() : null;
        public CancellationTokenSource? Stop { get; set; }
        public List<INatsSub<byte[]>> Subscriptions { get; } = [];
        public List<Task> Consumers { get; } = [];

        public async Task CloseAsync()
        {
            if (Stop is not { } stop) return;
            Stop = null;
            using (stop)
            {
                await stop.CancelAsync();
                try { await Task.WhenAll(Subscriptions.Select(subscription => subscription.DisposeAsync().AsTask())); }
                finally
                {
                    await Task.WhenAll(Consumers);
                    Subscriptions.Clear();
                    Consumers.Clear();
                }
            }
        }
    }

    internal async Task<string> AuthorizeAsync(string publicKey, CancellationToken cancellationToken)
    {
        var access = await tokens.GetAccessTokenAsync(cancellationToken);
        // The slash-suffixed URL redirects (307); HttpClient strips bearer auth
        // on redirects. Use the canonical route and never forward credentials
        // to an unvalidated redirect target.
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/api/apex/v1/socket/token/create")
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
        Action onGap, Action<HistoricalFeedInstrument, string, DateTimeOffset> onInvalidPoint,
        CancellationTokenSource lifetime, CancellationToken subscriptionToken)
    {
        try
        {
            await foreach (var message in subscription.Msgs.ReadAllAsync(subscriptionToken))
            {
                var received = clock.GetUtcNow();
                DispatchFrame(message.Data ?? [], instrument, kind, received, onPoint,
                    () => onInvalidPoint(instrument, kind, received));
            }
            if (!subscriptionToken.IsCancellationRequested) throw new InvalidDataException("A feed subscription ended before the capture window closed.");
        }
        catch (OperationCanceledException) when (subscriptionToken.IsCancellationRequested) { }
        catch
        {
            onGap();
            await lifetime.CancelAsync();
            throw;
        }
    }

    internal static void DispatchFrame(byte[] data, HistoricalFeedInstrument instrument, string kind,
        DateTimeOffset received, Action<HistoricalFeedInstrument, string, GrowwFeedPoint, DateTimeOffset> onPoint,
        Action onInvalid)
    {
        GrowwFeedPoint point;
        try { point = GrowwHistoricalFeedCodec.Decode(data, kind, received, instrument.Exchange, instrument.Segment); }
        catch (Exception exception) when (exception is InvalidDataException or OverflowException)
        {
            // A bad frame must invalidate its own cached side, not disconnect
            // every other subject. The gap is retained; no fabricated quote or
            // successful replay coverage is inferred. Transport failures and
            // the existing 30-second silence watchdog still reconnect.
            onInvalid();
            return;
        }
        onPoint(instrument, kind, point, received);
    }
}
