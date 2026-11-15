using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TradingSystem.Application.Broker;
using TradingSystem.Application.Execution;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.MarketData;

namespace TradingSystem.ContractTests;

public sealed class UpstoxReplayBackupTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 16, 9, 30, 0, TimeSpan.FromHours(5.5));
    private static readonly HistoricalFeedInstrument Instrument = new("SENSEX", "BSE", "FNO", "12345", "SENSEXCE",
        InstrumentType.CallOption, new(2026, 11, 19), 82000m, 20, .05m);

    [Fact]
    public void ContractIdentityChecksTheDatedPlanRatherThanTheDisplayName()
    {
        var expiry = new DateTimeOffset(2026, 11, 19, 0, 0, 0, TimeSpan.FromHours(5.5)).ToUnixTimeMilliseconds();
        Assert.True(UpstoxReplayBackupCapture.Matches(Instrument, "SENSEX", "CE", 82000m, 20, expiry));
        Assert.False(UpstoxReplayBackupCapture.Matches(Instrument, "SENSEX", "PE", 82000m, 20, expiry));
        Assert.False(UpstoxReplayBackupCapture.Matches(Instrument, "SENSEX", "CE", 82000m, 10, expiry));
        Assert.False(UpstoxReplayBackupCapture.Matches(Instrument, "BANKNIFTY", "CE", 82000m, 20, expiry));
        Assert.False(UpstoxReplayBackupCapture.Matches(Instrument, "SENSEX", "CE", 82000m, 20, expiry + 86_400_000));
    }

    [Fact]
    public void ParsesRealBookAndRejectsStaleFutureAndCrossedObservations()
    {
        using var json = JsonDocument.Parse(Response(At.AddSeconds(-1), At.AddSeconds(-2), 102m));
        Assert.True(UpstoxReplayBackupCapture.TryRead(json.RootElement, At, out var key, out var quote, out var observed));
        Assert.Equal("BSE_FO|12345", key);
        Assert.Equal(At.AddSeconds(-1), observed);
        Assert.Equal(At.AddSeconds(-2).ToUnixTimeMilliseconds(), quote.LastTradeTimeEpochMilliseconds);
        Assert.Equal(99m, quote.BidPrice);
        Assert.Equal(102m, quote.OfferPrice);
        Assert.Equal(40, quote.BidQuantity);
        Assert.Equal(60, quote.OfferQuantity);
        foreach (var invalid in new[] { Response(At.AddSeconds(1), At.AddSeconds(-2), 102m),
            Response(At.AddSeconds(-1), At.AddSeconds(-31), 102m), Response(At, At, 98m) })
        {
            using var rejected = JsonDocument.Parse(invalid);
            Assert.False(UpstoxReplayBackupCapture.TryRead(rejected.RootElement, At, out _, out _, out _));
        }
    }

    [Fact]
    public void FreshGrowwIncludingAnExplicitEmptyBookTakesPrecedenceUntilInvalidated()
    {
        using var archive = new HistoricalQuoteArchive(TimeProvider.System, NullLogger<HistoricalQuoteArchive>.Instance);
        var primary = new GrowwHistoricalFeedAccumulator(archive);
        Assert.True(primary.NeedsBackup(Instrument, At));
        primary.Observe(Instrument, "price", new(At, 100m, 2000m, 500m, null, null, null, null), At);
        primary.Observe(Instrument, "depth", new(At, null, null, null, null, null, null, null), At);
        Assert.False(primary.NeedsBackup(Instrument, At.AddSeconds(15)));
        Assert.True(primary.NeedsBackup(Instrument, At.AddSeconds(31)));
        primary.Invalidate(Instrument, "depth", At.AddSeconds(1));
        Assert.True(primary.NeedsBackup(Instrument, At.AddSeconds(1)));
    }

    [Fact]
    public async Task CaptureWarmsAllPlannedContractsAndPersistsOnlyMissingPrimary()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var healthy = Instrument with { ExchangeToken = "56789", TradingSymbol = "SENSEXALTCE" };
        var clock = new TestClock();
        var recorded = new TaskCompletionSource<HistoricalQuoteObservation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var archive = new CapturedBackup(recorded);
        var primary = new GrowwHistoricalFeedAccumulator(archive);
        primary.Observe(healthy, "price", new(At, 100m, null, null, null, null, null, null), At);
        primary.Observe(healthy, "depth", new(At, null, null, null, 99m, 40, 102m, 60), At);
        using var handler = new TestApi();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["ReplayBackup:UpstoxAnalyticsToken"] = "fixture-not-a-real-token" }).Build();
        var capture = new UpstoxReplayBackupCapture(new TestClientFactory(handler), config, archive, clock,
            NullLogger<UpstoxReplayBackupCapture>.Instance);
        var task = capture.RunAsync([Instrument, healthy], primary, At.AddMinutes(-15), At.AddMinutes(1), cancellation.Token);
        var observation = await recorded.Task.WaitAsync(cancellation.Token);
        await cancellation.CancelAsync();
        await task;
        Assert.Equal("BSE_FO|12345,BSE_FO|56789", handler.RequestedKey);
        Assert.False(handler.CatalogHadAuthorization);
        Assert.Equal(Instrument.TradingSymbol, observation.Request.TradingSymbol);
        Assert.Equal(HistoricalQuoteObservation.UpstoxSnapshot, observation.Source);
        Assert.Null(observation.DepthSourceAtUtc);
        Assert.Equal(At.AddSeconds(-1), observation.BrokerObservedAtUtc);
        Assert.True(HistoricalQuoteProjection.IsValidBackup(observation));
    }

    private sealed class TestClock : TimeProvider { public override DateTimeOffset GetUtcNow() => At; }

    private sealed class CapturedBackup(TaskCompletionSource<HistoricalQuoteObservation> received) : IHistoricalQuoteArchive
    {
        public void TryRecord(HistoricalQuoteObservation observation) => throw new NotSupportedException();
        public bool TryRecordBackup(HistoricalQuoteObservation observation) => received.TrySetResult(observation);
        public void MarkCaptureGap(DateOnly tradingDate) => throw new NotSupportedException();
        public Task<HistoricalQuoteArchiveDay> ReadAsync(DateOnly tradingDate, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class TestClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class TestApi : HttpMessageHandler
    {
        private static readonly string[] ExchangeTokens = ["12345", "56789"];
        public string? RequestedKey { get; private set; }
        public bool CatalogHadAuthorization { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.Host == "assets.upstox.com")
            {
                CatalogHadAuthorization = request.Headers.Authorization is not null;
                var rows = ExchangeTokens.Select(token => new
                {
                    segment = "BSE_FO", exchange_token = token, instrument_key = "BSE_FO|" + token,
                    underlying_symbol = "SENSEX", instrument_type = "CE", strike_price = 82000m, lot_size = 20,
                    expiry = new DateTimeOffset(2026, 11, 19, 0, 0, 0, TimeSpan.FromHours(5.5)).ToUnixTimeMilliseconds()
                });
                using var target = new MemoryStream();
                using (var gzip = new GZipStream(target, CompressionMode.Compress, leaveOpen: true))
                    gzip.Write(JsonSerializer.SerializeToUtf8Bytes(rows));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(target.ToArray()) });
            }
            RequestedKey = Uri.UnescapeDataString(request.RequestUri?.Query.Split('=').Last() ?? "");
            var body = "{\"data\":{\"BSE_FO:SENSEXCE\":" + Response(At.AddSeconds(-1), At.AddSeconds(-2), 102m) + "}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private static string Response(DateTimeOffset observed, DateTimeOffset trade, decimal ask) => JsonSerializer.Serialize(new
    {
        instrument_token = "BSE_FO|12345", timestamp = observed, last_trade_time = trade.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
        last_price = 100.75m, volume = 2000, oi = 500, depth = new
        {
            buy = new[] { new { price = 98m, quantity = 50 }, new { price = 99m, quantity = 40 } },
            sell = new[] { new { price = ask, quantity = 60 } }
        }
    });
}
