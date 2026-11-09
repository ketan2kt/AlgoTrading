using System.Buffers.Binary;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NATS.NKeys;
using TradingSystem.Application.Broker;
using TradingSystem.Application.Execution;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Broker.Groww;
using TradingSystem.Infrastructure.MarketData;

namespace TradingSystem.ContractTests;

public sealed class GrowwHistoricalFeedContractTests : IDisposable
{
    private static readonly DateTimeOffset At = new(2026, 11, 4, 4, 0, 0, TimeSpan.Zero);
    private readonly string directory = Directory.CreateTempSubdirectory("sarthi-feed-contract-").FullName;
    private static readonly HistoricalFeedInstrument Instrument = new("NIFTY", "NSE", "FNO", "1234", "NIFTYCE",
        InstrumentType.CallOption, new(2026, 11, 10), 22000m, 65, .05m);

    [Fact]
    public void PulseDoesNotCarrySubscriptionOrReceiptsAcrossRetriesOrDates()
    {
        var pulse = new ResearchCapturePulse();
        var date = new DateOnly(2026, 11, 4);
        pulse.Starting(date);
        pulse.Subscribed(At, 8);
        pulse.Received("NIFTY", At.AddSeconds(1));
        pulse.Received("NIFTY", At);
        Assert.Equal(At.AddSeconds(1), pulse.Read(date,true).LastReceiptByMarket["NIFTY"]);
        pulse.Failed();
        pulse.Starting(date);
        var retry = pulse.Read(date,true);
        Assert.True(retry.GapObserved);
        Assert.Null(retry.SubscribedAtUtc);
        Assert.Empty(retry.LastReceiptByMarket);
        Assert.Equal("Not started", pulse.Read(date.AddDays(1),true).Phase);
        Assert.Equal("Disabled", pulse.Read(date,false).Phase);
    }

    [Fact]
    public async Task UniverseSnapshotUsesOneReceiptTimeAndNeverExposesAnUnreceivedTick()
    {
        using var archive = new HistoricalQuoteArchive(TimeProvider.System, NullLogger<HistoricalQuoteArchive>.Instance, directory);
        var accumulator = new GrowwHistoricalFeedAccumulator(archive);
        var index = Instrument with { TradingSymbol = "NIFTY", ExchangeToken = "999", Type = InstrumentType.Index, Segment = "CASH" };
        accumulator.Observe(index, "index", new(At, 22000, null, null, null, null, null, null), At);
        accumulator.Observe(Instrument, "price", new(At, 100, 1000, 5000, null, null, null, null), At.AddMilliseconds(100));
        accumulator.Observe(Instrument, "depth", new(At, null, null, null, 99, 65, 101, 65), At.AddMilliseconds(200));
        accumulator.Sample(At.AddMilliseconds(50));
        accumulator.Sample(At.AddSeconds(2));
        await archive.FlushBatchAsync(CancellationToken.None);
        var rows = (await archive.ReadAsync(new(2026, 11, 4), CancellationToken.None)).Observations;
        Assert.Single(rows, value => value.ReceivedAtUtc == At.AddMilliseconds(50));
        Assert.Equal(2, rows.Count(value => value.ReceivedAtUtc == At.AddSeconds(2)));
        Assert.All(rows, value => Assert.Equal(At.ToUnixTimeMilliseconds(), value.Quote.LastTradeTimeEpochMilliseconds));
    }

    [Fact]
    public void AfterMarketRepairNeverCompetesWithDaytimePollingAndCatchesPreviousWeekday()
    {
        DateTimeOffset Ist(int day, int hour, int minute) => new(2026, 11, day, hour, minute, 0, TimeSpan.FromHours(5.5));
        Assert.Null(HistoricalAfterMarketBackfillService.DueDate(Ist(4, 15, 59)));
        Assert.Null(HistoricalAfterMarketBackfillService.DueDate(Ist(4, 8, 45)));
        Assert.Equal(new DateOnly(2026, 11, 4), HistoricalAfterMarketBackfillService.DueDate(Ist(4, 16, 0)));
        Assert.Equal(new DateOnly(2026, 11, 3), HistoricalAfterMarketBackfillService.DueDate(Ist(4, 8, 44)));
        Assert.Equal(new DateOnly(2026, 10, 30), HistoricalAfterMarketBackfillService.DueDate(Ist(2, 7, 0)));
        Assert.Equal(new DateOnly(2026, 11, 6), HistoricalAfterMarketBackfillService.DueDate(Ist(8, 17, 0)));
    }

    [Fact]
    public void OfficialProtobufPriceDepthAndIndexFieldsDecodeWithObservedSidesAndOi()
    {
        var price = GrowwHistoricalFeedCodec.Decode(Envelope(4, Join(Double(1, At.ToUnixTimeMilliseconds()),
            Double(13, 100), Double(6, 1000), Double(14, 5000))), "price", At, "NSE", "FNO");
        Assert.Equal(100, price.Last);
        Assert.Equal(1000, price.Volume);
        Assert.Equal(5000, price.OpenInterest);
        var depth = GrowwHistoricalFeedCodec.Decode(Envelope(5, Join(Double(1, At.ToUnixTimeSeconds()),
            Chunk(2, Book(99.95, 65)), Chunk(3, Book(100.05, 130)))), "depth", At, "NSE", "FNO");
        Assert.Equal(99.95m, depth.Bid);
        Assert.Equal(100.05m, depth.Offer);
        Assert.Equal(At, depth.SourceAtUtc); // Explicit seconds-to-milliseconds normalization.
        var index = GrowwHistoricalFeedCodec.Decode(Join(Varint(3, 1), Chunk(6,
            Join(Double(1, At.ToUnixTimeMilliseconds()), Double(2, 22500)))), "index", At, "NSE", "CASH");
        Assert.Equal(22500, index.Last);
    }

    [Fact]
    public void MissingSidesFutureTimestampsWrongMarketAndMalformedFramesAreRejected()
    {
        var crossed = Envelope(5, Join(Double(1, At.ToUnixTimeMilliseconds()), Chunk(2, Book(101, 10)), Chunk(3, Book(100, 10))));
        Assert.Throws<InvalidDataException>(() => GrowwHistoricalFeedCodec.Decode(crossed, "depth", At, "NSE", "FNO"));
        var future = Envelope(4, Join(Double(1, At.AddMinutes(1).ToUnixTimeMilliseconds()), Double(13, 100)));
        Assert.Throws<InvalidDataException>(() => GrowwHistoricalFeedCodec.Decode(future, "price", At, "NSE", "FNO"));
        var valid = Envelope(4, Join(Double(1, At.ToUnixTimeMilliseconds()), Double(13, 100)));
        Assert.Throws<InvalidDataException>(() => GrowwHistoricalFeedCodec.Decode(valid, "price", At, "BSE", "FNO"));
        Assert.Throws<InvalidDataException>(() => GrowwHistoricalFeedCodec.Decode([0x22, 0x7f, 0x00], "price", At, "NSE", "FNO"));
    }

    [Fact]
    public async Task WithdrawnBookClearsOldLiquidityWithoutBreakingOtherInstrumentRecording()
    {
        var empty = GrowwHistoricalFeedCodec.Decode(Envelope(5, Join(Double(1, At.ToUnixTimeMilliseconds()),
            Chunk(2, Book(0, 0)), Chunk(3, Book(0, 0)))), "depth", At, "NSE", "FNO");
        Assert.Null(empty.Bid);
        Assert.Null(empty.Offer);
        using var archive = new HistoricalQuoteArchive(TimeProvider.System, NullLogger<HistoricalQuoteArchive>.Instance, directory);
        var accumulator = new GrowwHistoricalFeedAccumulator(archive);
        accumulator.Observe(Instrument, "price", new(At, 100, 1000, 5000, null, null, null, null), At);
        accumulator.Observe(Instrument, "depth", new(At, null, null, null, 99, 65, 101, 65), At);
        accumulator.Sample(At);
        accumulator.Observe(Instrument, "depth", empty, At.AddSeconds(1));
        accumulator.Sample(At.AddSeconds(2));
        await archive.FlushBatchAsync(CancellationToken.None);
        var path = await archive.ReadReplayAsync(new(2026, 11, 4), "NIFTY", "NSE",
            new Dictionary<string,string> { [Instrument.TradingSymbol] = Instrument.TradingSymbol }, At, At.AddMinutes(1), CancellationToken.None);
        Assert.False(path.HasCaptureGaps);
        Assert.Equal(2, path.Quotes.Count);
        Assert.Equal(99, path.Quotes[0].BidPrice);
        Assert.Null(path.Quotes[1].BidPrice);
        Assert.NotNull(path.Quotes[1].DepthSourceAtUtc);
        var health = archive.ReadHealth(new(2026, 11, 4));
        Assert.Equal(0, health.Buffered);
        Assert.NotNull(health.LastPersistedAtUtc);
        Assert.Null(health.Failure);
    }

    [Fact]
    public void SilentMarketIsDetectedEvenIfOtherMarketsStillReceiveTicks()
    {
        var pulse = new ResearchCapturePulse();
        var date = new DateOnly(2026, 11, 4);
        pulse.Starting(date);
        pulse.Subscribed(At, 995);
        pulse.Received("NIFTY", At.AddSeconds(31));
        pulse.Received("BANKNIFTY", At.AddSeconds(31));
        Assert.True(pulse.Read(date, true).HasStalledMarket(At.AddSeconds(31)));
        pulse.Received("SENSEX", At.AddSeconds(31));
        Assert.False(pulse.Read(date, true).HasStalledMarket(At.AddSeconds(31)));
        pulse.Stage("Recording"); pulse.Failed("TimeoutException"); pulse.Starting(date);
        Assert.Equal("Recording: TimeoutException", pulse.Read(date, true).LastFailure);
    }

    [Fact]
    public async Task SeparatePriceAndDepthUpdatesProducePersistedTwoSidedSamplesWithoutManufacturingQuotes()
    {
        using var archive = new HistoricalQuoteArchive(TimeProvider.System, NullLogger<HistoricalQuoteArchive>.Instance, directory);
        var accumulator = new GrowwHistoricalFeedAccumulator(archive);
        var price = new GrowwFeedPoint(At, 100, 1000, 5000, null, null, null, null);
        var depth = new GrowwFeedPoint(At, null, null, null, 99, 65, 101, 65);
        accumulator.Observe(Instrument, "price", price, At);
        accumulator.Sample(At);
        await archive.FlushBatchAsync(CancellationToken.None);
        Assert.Empty((await archive.ReadAsync(new(2026, 11, 4), CancellationToken.None)).Observations);
        accumulator.Observe(Instrument, "depth", depth, At.AddMilliseconds(10));
        accumulator.Sample(At.AddMilliseconds(10));
        accumulator.Observe(Instrument, "price", price with { Last = 102 }, At.AddSeconds(1));
        accumulator.Observe(Instrument, "depth", depth, At.AddSeconds(3));
        accumulator.Sample(At.AddSeconds(3));
        await archive.FlushBatchAsync(CancellationToken.None);
        var observations = (await archive.ReadAsync(new(2026, 11, 4), CancellationToken.None)).Observations;
        Assert.Equal(2, observations.Count);
        Assert.Equal(100, observations[0].Quote.LastPrice);
        Assert.Equal(102, observations[1].Quote.LastPrice);
        Assert.Equal(99, observations[1].Quote.BidPrice);
        Assert.Equal("GrowwFeedSample", observations[0].Source);
        Assert.Equal(At, observations[0].DepthSourceAtUtc);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SocketAuthorizationUsesExistingBearerAndEphemeralPublicKeyOnly(bool wrapped)
    {
        using var key = KeyPair.CreatePair(PrefixByte.User);
        var handler = new Handler(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1/api/apex/v1/socket/token/create", request.RequestUri!.AbsolutePath);
            Assert.Equal("test-access", request.Headers.Authorization!.Parameter);
            var json = await request.Content!.ReadAsStringAsync();
            Assert.Contains(key.GetPublicKey(), json);
            Assert.DoesNotContain(key.GetSeed(), json);
            return new(HttpStatusCode.OK) { Content = new StringContent(wrapped ?
                "{\"status\":\"SUCCESS\",\"payload\":{\"token\":\"test-socket-jwt\"}}" :
                "{\"token\":\"test-socket-jwt\"}", Encoding.UTF8, "application/json") };
        });
        using var client = new HttpClient(handler) { BaseAddress = new("https://api.groww.in/") };
        var transport = new GrowwHistoricalFeedTransport(new ClientFactory(client), new TokenProvider(), TimeProvider.System);
        Assert.Equal("test-socket-jwt", await transport.AuthorizeAsync(key.GetPublicKey(), CancellationToken.None));
        Assert.Equal("/ld/fo/nse/price_detailed.1234", GrowwHistoricalFeedTransport.Subject(Instrument, "price"));
        Assert.Equal("/ld/fo/nse/book.1234", GrowwHistoricalFeedTransport.Subject(Instrument, "depth"));
    }

    [Theory]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task SocketAuthorizationFailureRetainsSafeHttpStatus(HttpStatusCode status)
    {
        var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(status)
            { Content = new StringContent("No credentials should enter telemetry") }));
        using var client = new HttpClient(handler) { BaseAddress = new("https://api.groww.in/") };
        var transport = new GrowwHistoricalFeedTransport(new ClientFactory(client), new TokenProvider(), TimeProvider.System);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => transport.AuthorizeAsync("test-public-key", CancellationToken.None));
        Assert.Equal(status, error.StatusCode);
        Assert.DoesNotContain("test-access", error.Message);
    }

    [Fact]
    public void PriceAndDepthBothCountAgainstConnectionBudgetAndBandsRemainBalanced()
    {
        Assert.Equal(1000, GrowwHistoricalFeedTransport.SubscriptionCount(Enumerable.Repeat(Instrument, 500).ToArray()));
        Assert.Throws<ArgumentException>(() => GrowwHistoricalFeedTransport.SubscriptionCount(Enumerable.Repeat(Instrument, 501).ToArray()));
        Assert.Throws<ArgumentException>(() => GrowwHistoricalFeedTransport.SubscriptionCount([]));
        var date = new DateOnly(2026, 11, 4);
        var markets = new[] { "NIFTY", "SENSEX", "BANKNIFTY" };
        var rows = new List<HistoricalFeedInstrument>();
        var token = 0;
        foreach (var market in markets)
        {
            var exchange = market == "SENSEX" ? "BSE" : "NSE";
            rows.Add(Instrument with { Market = market, Exchange = exchange, Segment = "CASH", TradingSymbol = market,
                Type = InstrumentType.Index, ExchangeToken = (++token).ToString(System.Globalization.CultureInfo.InvariantCulture), StrikePrice = null, ExpiryDate = null });
            if (market != "BANKNIFTY") rows.Add(Instrument with { Market = market, Exchange = exchange, TradingSymbol = market + "FUT",
                Type = InstrumentType.Future, ExchangeToken = (++token).ToString(System.Globalization.CultureInfo.InvariantCulture), StrikePrice = null, ExpiryDate = date.AddDays(20) });
            for (var i = 0; i < 200; i++)
            foreach (var type in new[] { InstrumentType.CallOption, InstrumentType.PutOption })
                rows.Add(Instrument with { Market = market, Exchange = exchange, Type = type, StrikePrice = 15000 + i * 50,
                    TradingSymbol = market + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + (type == InstrumentType.CallOption ? "CE" : "PE"),
                    ExchangeToken = (++token).ToString(System.Globalization.CultureInfo.InvariantCulture) });
        }
        var plan = HistoricalFeedSubscriptionPlanner.Select(date, rows, 500, markets.ToDictionary(market => market, _ => 22000m));
        Assert.Equal(497, plan.Count);
        Assert.Equal(991, GrowwHistoricalFeedTransport.SubscriptionCount(plan));
        foreach (var market in markets)
            Assert.Equal(164, plan.Count(row => row.Market == market && row.Type is InstrumentType.CallOption or InstrumentType.PutOption));
    }

    [Fact]
    public async Task HealthReportsOnlyDurablyWrittenOptionQuotesAndTheirRealPriceDepthTimes()
    {
        var date = new DateOnly(2026, 11, 4);
        using var archive = new HistoricalQuoteArchive(new RecordingClock(At), NullLogger<HistoricalQuoteArchive>.Instance, directory);
        void Add(string market, bool option, DateTimeOffset priceAt) => archive.TryRecord(new(Guid.NewGuid(), At,
            new(market == "SENSEX" ? "BSE" : "NSE", option ? "FNO" : "CASH", market + (option ? "CE" : "")),
            new(100m, priceAt.ToUnixTimeMilliseconds(), 99m, 30L, 101m, 30L, null, null, null))
            { Source = "GrowwFeedSample", DepthSourceAtUtc = At });
        Add("NIFTY", true, At); Add("SENSEX", true, At); Add("BANKNIFTY", false, At);
        Assert.Empty(archive.ReadHealth(date).LastOptionWrittenAtByMarket);
        await archive.FlushBatchAsync(CancellationToken.None);
        Assert.Equal(2, archive.ReadHealth(date).LastOptionWrittenAtByMarket.Count);
        Assert.False(archive.ReadHealth(date).LastOptionWrittenAtByMarket.ContainsKey("BANKNIFTY"));
        Add("BANKNIFTY", true, At.AddSeconds(-31));
        await archive.FlushBatchAsync(CancellationToken.None);
        Assert.Equal(At, archive.ReadHealth(date).LastOptionWrittenAtByMarket["BANKNIFTY"]);
        Assert.Equal(At.AddSeconds(-31), archive.ReadHealth(date).LastOptionSourceAtByMarket["BANKNIFTY"]);
        Add("BANKNIFTY", true, At);
        await archive.FlushBatchAsync(CancellationToken.None);
        Assert.Equal(At, archive.ReadHealth(date).LastOptionSourceAtByMarket["BANKNIFTY"]);
    }

    private sealed class RecordingClock(DateTimeOffset at) : TimeProvider { public override DateTimeOffset GetUtcNow() => at; }

    private static byte[] Envelope(int field, byte[] payload) => Join(Varint(2, 1), Varint(3, 1), Chunk(field, payload));
    private static byte[] Book(double price, double quantity) => Join(Varint(1, 1), Chunk(2, Join(Double(1, price), Double(2, quantity))));
    private static byte[] Join(params byte[][] fields) => fields.SelectMany(value => value).ToArray();
    private static byte[] Double(int field, double value)
    {
        var bytes = new byte[9]; bytes[0] = (byte)(field * 8 + 1);
        BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(1), value); return bytes;
    }
    private static byte[] Chunk(int field, byte[] value) => Join(Integer((ulong)(field * 8 + 2)), Integer((ulong)value.Length), value);
    private static byte[] Varint(int field, ulong value) => Join(Integer((ulong)(field * 8)), Integer(value));
    private static byte[] Integer(ulong value)
    {
        var bytes = new List<byte>();
        do { var current = (byte)(value & 127); value >>= 7; bytes.Add(value == 0 ? current : (byte)(current | 128)); } while (value != 0);
        return bytes.ToArray();
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => respond(request);
    }
    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory { public HttpClient CreateClient(string name) => client; }
    private sealed class TokenProvider : IGrowwAccessTokenProvider
    { public ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken) => ValueTask.FromResult("test-access"); }
    public void Dispose() => Directory.Delete(directory, recursive: true);
}
