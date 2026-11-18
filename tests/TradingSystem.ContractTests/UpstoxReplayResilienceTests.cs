using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TradingSystem.Application.Broker;
using TradingSystem.Application.Execution;
using TradingSystem.Infrastructure.MarketData;
using TradingSystem.Domain.Trading;

namespace TradingSystem.ContractTests;

public sealed class UpstoxReplayResilienceTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 16, 9, 15, 0, TimeSpan.FromHours(5.5));
    private static readonly string[] Markets = ["NIFTY", "SENSEX", "BANKNIFTY"];
    private static HistoricalFeedInstrument Instrument(string market, int index = 1) => new(market,
        market == "SENSEX" ? "BSE" : "NSE", "FNO", index.ToString(System.Globalization.CultureInfo.InvariantCulture), market + index + "CE",
        InstrumentType.CallOption, new(2026, 11, 23), 55000, 30, .05m);
    private static string Key(HistoricalFeedInstrument instrument) => instrument.Exchange + "_FO|" + instrument.ExchangeToken;

    [Fact]
    public void ExpiryMetadataIsAdvisoryAndMalformedOrOpaqueTokensStaySupported()
    {
        var payload = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("{\"exp\":1794826800}"));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1794826800), UpstoxReplayBackupCapture.CredentialExpiry("x." + payload + ".y"));
        Assert.Null(UpstoxReplayBackupCapture.CredentialExpiry("opaque-token"));
        Assert.Null(UpstoxReplayBackupCapture.CredentialExpiry("x.%%%.y"));
    }

    [Fact]
    public async Task LostBackupLeaseCancelsCaptureAndReleasesOwnership()
    {
        var stopped = false;
        var released = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReplayBlobStorage.RunOwnedBackupAsync(async token =>
        {
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { stopped = true; }
        }, _ => throw new InvalidOperationException("Lease lost"), _ =>
        {
            Assert.True(stopped);
            released = true;
            return Task.CompletedTask;
        }, new FastClock(), CancellationToken.None));
        Assert.True(released);
    }

    [Fact]
    public async Task TransientLeaseFailureRecoversWithoutRestartingCapture()
    {
        var clock = new FastClock();
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var captureStarts = 0;
        var renewals = 0;
        var released = false;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await ReplayBlobStorage.RunOwnedBackupAsync(async token =>
        {
            captureStarts++;
            await recovered.Task.WaitAsync(token);
        }, _ =>
        {
            if (++renewals == 1) throw new HttpRequestException("Transient fixture");
            recovered.TrySetResult();
            return Task.CompletedTask;
        }, _ => { released = true; return Task.CompletedTask; }, clock, stop.Token);
        Assert.Equal(1, captureStarts);
        Assert.True(renewals >= 2);
        Assert.True(released);
        Assert.Contains(TimeSpan.FromSeconds(2), clock.Delays);
    }

    [Fact]
    public async Task RepeatedLeaseFailureStopsBeforeOwnershipExpires()
    {
        var clock = new FastClock();
        var stopped = false;
        var released = false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<HttpRequestException>(() => ReplayBlobStorage.RunOwnedBackupAsync(async token =>
        {
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { stopped = true; }
        }, _ =>
        {
            clock.Advance(TimeSpan.FromSeconds(15));
            throw new HttpRequestException("Transient fixture");
        }, _ => { Assert.True(stopped); released = true; return Task.CompletedTask; }, clock, timeout.Token));
        Assert.True(released);
        Assert.True(clock.GetUtcNow() - Start < TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task CatalogIsReusedButChangedPlansAreRemappedAndNewDatesReload()
    {
        var first = Instrument("NIFTY", 1);
        var second = Instrument("BANKNIFTY", 2);
        using var api = new Api([first, second], (_, _) => throw new InvalidOperationException("No quote calls expected"));
        using var client = new HttpClient(api, disposeHandler: false);
        using var capture = Capture(api, new Archive(), new FastClock());
        var day = new DateOnly(2026, 11, 18);
        Assert.Single(await capture.LoadPlanAsync(client, [first], day, CancellationToken.None));
        var changed = await capture.LoadPlanAsync(client, [second], day, CancellationToken.None);
        Assert.Single(changed);
        Assert.Contains(Key(second), changed.Keys);
        Assert.Equal(1, api.CatalogRequests);
        Assert.Empty(await capture.LoadPlanAsync(client, [second with { StrikePrice = 56000 }], day, CancellationToken.None));
        Assert.Single(await capture.LoadPlanAsync(client, [first], day.AddDays(1), CancellationToken.None));
        Assert.Equal(2, api.CatalogRequests);
    }

    [Fact]
    public async Task IndependentCollectorPersistsHealthyPrimaryOnAllMarkets()
    {
        foreach (var market in Markets)
        {
            var item = Instrument(market);
            var clock = new FastClock();
            var archive = new Archive();
            var primary = new GrowwHistoricalFeedAccumulator(archive);
            primary.Observe(item, "price", new(Start, 100, null, null, null, null, null, null), Start);
            primary.Observe(item, "depth", new(Start, null, null, null, 99, 300, 101, 300), Start);
            using var api = new Api([item], (_, _) =>
            {
                clock.Advance(TimeSpan.FromSeconds(3));
                return Task.FromResult(Quotes(item, clock.GetUtcNow(), 100));
            });
            using var token = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await Capture(api, archive, clock).RunAsync([item], primary, Start, Start.AddSeconds(8), token.Token, persistAll: true);
            Assert.Equal(2, archive.Rows.Count);
            Assert.All(archive.Rows, row => Assert.Equal(HistoricalQuoteObservation.UpstoxSnapshot, row.Source));
        }
    }

    [Fact]
    public async Task PreOpenChecksCredentialsAndDoesNotArchiveYesterdayQuotes()
    {
        var item = Instrument("SENSEX");
        var clock = new FastClock();
        clock.Advance(TimeSpan.FromMinutes(-2));
        var archive = new Archive();
        var calls = 0;
        using var token = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var api = new Api([item], (_, _) =>
        {
            calls++;
            clock.Advance(TimeSpan.FromSeconds(10));
            if (calls == 2) token.Cancel();
            return Task.FromResult(calls == 1 ? new HttpResponseMessage(HttpStatusCode.Unauthorized) : Quotes(item, Start.AddDays(-1), 100));
        });
        await Capture(api, archive, clock).RunAsync([item], new(archive), Start, Start.AddMinutes(1), token.Token, persistAll: true);
        Assert.True(archive.Failed.Task.IsCompleted);
        Assert.Equal(2, calls);
        Assert.Empty(archive.Rows);
        Assert.Contains(TimeSpan.FromMinutes(1), clock.Delays);
    }

    [Theory]
    [InlineData("ReplayRecorder", false)]
    [InlineData("Website", true)]
    public void SingleBackupHostIsEnforced(string role, bool enabled)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ApplicationRole"] = role, ["ReplayBackup:Host"] = "Website", ["ReplayBackup:UpstoxAnalyticsToken"] = "test-only"
        }).Build();
        Assert.Equal(enabled, UpstoxIndependentCaptureService.Enabled(config));
        using var api = new Api([], (_, _) => throw new NotSupportedException());
        var capture = new UpstoxReplayBackupCapture(new Clients(api), config, new Archive(), new FastClock(), NullLogger<UpstoxReplayBackupCapture>.Instance);
        Assert.Equal(enabled, capture.Enabled);
    }

    [Fact]
    public async Task WebsitePrimaryFallbackCannotStartDuplicateBackupButIndependentOwnerRecordsAllThree()
    {
        var instruments = Markets.Select((market, index) => Instrument(market, index + 1)).ToArray();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ReplayBackup:Host"] = "Website", ["ReplayBackup:UpstoxAnalyticsToken"] = "test-only"
        }).Build();
        var clock = new FastClock();
        var archive = new Archive();
        using var api = new Api(instruments, (request, _) =>
        {
            var item = instruments.Single(item => Uri.UnescapeDataString(request.RequestUri!.Query).Contains(Key(item), StringComparison.Ordinal));
            clock.Advance(TimeSpan.FromSeconds(3));
            return Task.FromResult(Quotes(item, clock.GetUtcNow(), 100));
        });
        var capture = new UpstoxReplayBackupCapture(new Clients(api), config, archive, clock, NullLogger<UpstoxReplayBackupCapture>.Instance);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await capture.RunAsync(instruments, new(archive), Start, Start.AddSeconds(30), cancellation.Token);
        Assert.Null(archive.Coverage); // Embedded path must not even load the catalog.
        Assert.Empty(archive.Rows);
        await capture.RunAsync(instruments, new(archive), Start, Start.AddSeconds(30), cancellation.Token, persistAll: true);
        Assert.Equal(3, archive.Rows.Select(row => row.Request.TradingSymbol).Distinct().Count());
        Assert.All(archive.Rows, row => Assert.Equal(HistoricalQuoteObservation.UpstoxSnapshot, row.Source));
    }

    [Theory]
    [InlineData("NIFTY")]
    [InlineData("SENSEX")]
    [InlineData("BANKNIFTY")]
    public async Task WarmWindowPreservesLeadInAndLtpChangesOnAnUnchangedBook(string market)
    {
        var instrument = Instrument(market);
        var clock = new FastClock();
        var archive = new Archive();
        var primary = new GrowwHistoricalFeedAccumulator(archive);
        primary.Observe(instrument, "price", new(Start, 100, null, null, null, null, null, null), Start);
        primary.Observe(instrument, "depth", new(Start, null, null, null, 99, 300, 101, 300), Start);
        var calls = 0;
        using var api = new Api([instrument], (_, _) =>
        {
            var at = clock.GetUtcNow();
            clock.Advance(TimeSpan.FromSeconds(3));
            return Task.FromResult(Quotes(instrument, at, 100 + ++calls));
        });
        using var token = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Capture(api, archive, clock).RunAsync([instrument], primary, Start, Start.AddSeconds(40), token.Token);
        var saved = archive.Rows.ToArray();
        Assert.True(calls >= 13);
        Assert.True(saved.Length >= 11);
        Assert.Equal(Start.AddSeconds(3), saved[0].ReceivedAtUtc); // Before the 30s detector fired.
        Assert.Equal(saved.Length, saved.Select(row => row.Quote.LastPrice).Distinct().Count());
        Assert.Single(saved.Select(row => row.BackupLiquidityAtUtc).Distinct());
        Assert.All(saved, row => Assert.True(HistoricalQuoteProjection.IsValidBackup(row)));
        Assert.Equal(1, archive.Coverage![market].Mapped);
        Assert.Empty(archive.Coverage[market].MissingContracts);
    }

    [Theory]
    [InlineData(503)]
    [InlineData(429)]
    [InlineData(401)]
    [InlineData(0)]
    public async Task BrokenMarketDoesNotBlockOtherIndexesAndBackoffIsHonored(int failure)
    {
        var plan = Markets.Select((market, i) => Instrument(market, i + 1)).ToArray();
        var clock = new FastClock();
        var archive = new Archive();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var api = new Api(plan, async (request, token) =>
        {
            var key = Uri.UnescapeDataString(request.RequestUri!.Query.Split('=').Last());
            var item = plan.Single(value => Key(value) == key);
            if (item.Market != "SENSEX") return Quotes(item, Start, 100);
            if (failure == 0) await Task.Delay(Timeout.Infinite, token);
            var response = new HttpResponseMessage((HttpStatusCode)failure);
            if (failure == 429) response.Headers.RetryAfter = new(TimeSpan.FromSeconds(12));
            return response;
        });
        var task = Capture(api, archive, clock).RunAsync(plan, new(archive), Start, Start.AddMinutes(1), cancellation.Token);
        await archive.TwoMarkets.Task.WaitAsync(cancellation.Token);
        // Allow the failing task to record its status/backoff independently.
        if (failure != 0)
        {
            await archive.Failed.Task.WaitAsync(cancellation.Token);
            await clock.BackoffScheduled.Task.WaitAsync(cancellation.Token);
        }
        await cancellation.CancelAsync();
        await task;
        Assert.Contains(archive.Rows, row => row.Request.TradingSymbol.StartsWith("NIFTY", StringComparison.Ordinal));
        Assert.Contains(archive.Rows, row => row.Request.TradingSymbol.StartsWith("BANKNIFTY", StringComparison.Ordinal));
        Assert.DoesNotContain(archive.Rows, row => row.Request.TradingSymbol.StartsWith("SENSEX", StringComparison.Ordinal));
        if (failure == 429) Assert.Contains(TimeSpan.FromSeconds(12), clock.Delays);
        if (failure == 401) Assert.Contains(TimeSpan.FromMinutes(1), clock.Delays);
    }

    [Fact]
    public async Task PartialCatalogReportsEveryMissingContractAndKeepsSupportedCapture()
    {
        var supported = Instrument("NIFTY");
        var missing = Instrument("SENSEX", 2);
        var archive = new Archive();
        var clock = new FastClock();
        using var token = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var api = new Api([supported], (_, _) =>
        {
            clock.Advance(TimeSpan.FromSeconds(3));
            return Task.FromResult(Quotes(supported, clock.GetUtcNow(), 100));
        });
        await Capture(api, archive, clock).RunAsync([supported, missing], new(archive), Start, Start.AddSeconds(5), token.Token);
        Assert.NotEmpty(archive.Rows);
        Assert.Equal(0, archive.Coverage!["SENSEX"].Mapped);
        Assert.Equal([missing.TradingSymbol], archive.Coverage["SENSEX"].MissingContracts);
    }

    [Fact]
    public async Task FullPlanUsesAtMostThreeRequestsPerCycleAndNeverExceedsKeyLimit()
    {
        var plan = Enumerable.Range(1, 500).Select(i => Instrument(Markets[i % 3], i)).ToArray();
        var archive = new Archive();
        var clock = new FastClock();
        var requested = new ConcurrentBag<int>();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var api = new Api(plan, (request, _) =>
        {
            var keys = Uri.UnescapeDataString(request.RequestUri!.Query.Split('=').Last()).Split(',');
            requested.Add(keys.Length);
            if (requested.Count >= 3) cancellation.Cancel();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"data\":{}}") });
        });
        await Capture(api, archive, clock).RunAsync(plan, new(archive), Start, Start.AddMinutes(1), cancellation.Token);
        Assert.Equal(500, archive.Coverage!.Values.Sum(value => value.Mapped));
        Assert.Equal(500, requested.Sum());
        Assert.All(requested, count => Assert.InRange(count, 1, 500));
    }

    [Fact]
    public async Task TemporaryArchivePressureRetriesOriginalObservationsWithoutDuplicates()
    {
        var item = Instrument("NIFTY");
        var clock = new FastClock();
        var archive = new Archive { RejectRemaining = 1 };
        using var token = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var api = new Api([item], (_, _) =>
        {
            clock.Advance(TimeSpan.FromSeconds(3));
            return Task.FromResult(Quotes(item, clock.GetUtcNow(), 100));
        });
        await Capture(api, archive, clock).RunAsync([item], new(archive), Start, Start.AddSeconds(8), token.Token);
        Assert.Equal([Start.AddSeconds(3), Start.AddSeconds(6)], archive.Rows.Select(row => row.ReceivedAtUtc));
        Assert.Equal(2, archive.Rows.Select(row => row.Id).Distinct().Count());
    }

    private static UpstoxReplayBackupCapture Capture(HttpMessageHandler api, IHistoricalQuoteArchive archive, TimeProvider clock) =>
        new(new Clients(api), new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["ReplayBackup:UpstoxAnalyticsToken"] = "test-only" }).Build(), archive, clock, NullLogger<UpstoxReplayBackupCapture>.Instance);

    private sealed class Clients(HttpMessageHandler handler) : IHttpClientFactory
    { public HttpClient CreateClient(string name) => new(handler, disposeHandler: false); }

    private sealed class FastClock : TimeProvider
    {
        private long ticks = Start.UtcTicks;
        public ConcurrentBag<TimeSpan> Delays { get; } = [];
        public TaskCompletionSource BackoffScheduled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        public void Advance(TimeSpan time) => Interlocked.Add(ref ticks, time.Ticks);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Delays.Add(dueTime);
            if (dueTime >= TimeSpan.FromSeconds(12)) BackoffScheduled.TrySetResult();
            return System.CreateTimer(callback, state, TimeSpan.FromMilliseconds(5), Timeout.InfiniteTimeSpan);
        }
    }

    private sealed class Archive : IHistoricalQuoteArchive
    {
        public ConcurrentQueue<HistoricalQuoteObservation> Rows { get; } = new();
        public int RejectRemaining { get; set; }
        public IReadOnlyDictionary<string, HistoricalBackupMarketCoverage>? Coverage { get; private set; }
        public TaskCompletionSource TwoMarkets { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Failed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void TryRecord(HistoricalQuoteObservation observation) => throw new NotSupportedException();
        public bool TryRecordBackup(HistoricalQuoteObservation observation)
        {
            if (RejectRemaining > 0) { RejectRemaining--; return false; }
            Rows.Enqueue(observation);
            if (Rows.Select(row => row.Request.TradingSymbol).Distinct().Count() >= 2) TwoMarkets.TrySetResult();
            return true;
        }
        public void SetBackupCoverage(DateOnly tradingDate, IReadOnlyDictionary<string, HistoricalBackupMarketCoverage> coverage) => Coverage = coverage;
        public void SetBackupFailure(DateOnly tradingDate, string market, string? failure) { if (failure is not null) Failed.TrySetResult(); }
        public void MarkCaptureGap(DateOnly tradingDate) => throw new NotSupportedException();
        public Task<HistoricalQuoteArchiveDay> ReadAsync(DateOnly tradingDate, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Api(IReadOnlyList<HistoricalFeedInstrument> catalog,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> quotes) : HttpMessageHandler
    {
        public int CatalogRequests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host != "assets.upstox.com") return quotes(request, cancellationToken);
            CatalogRequests++;
            var rows = catalog.Select(item => new { segment = item.Exchange + "_FO", exchange_token = item.ExchangeToken,
                instrument_key = Key(item), underlying_symbol = item.Market, instrument_type = "CE", strike_price = item.StrikePrice,
                lot_size = item.LotSize, expiry = new DateTimeOffset(item.ExpiryDate!.Value.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(5.5)).ToUnixTimeMilliseconds() });
            using var stream = new MemoryStream();
            using (var gzip = new GZipStream(stream, CompressionMode.Compress, leaveOpen: true)) gzip.Write(JsonSerializer.SerializeToUtf8Bytes(rows));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(stream.ToArray()) });
        }
    }

    private static HttpResponseMessage Quotes(HistoricalFeedInstrument item, DateTimeOffset at, decimal price) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { data = new Dictionary<string, object>
        {
            [Key(item)] = new { instrument_token = Key(item), timestamp = at,
                last_trade_time = at.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture), last_price = price,
                depth = new { buy = new[] { new { price = 99m, quantity = 300 } }, sell = new[] { new { price = 101m, quantity = 300 } } } }
        } }))
    };
}
