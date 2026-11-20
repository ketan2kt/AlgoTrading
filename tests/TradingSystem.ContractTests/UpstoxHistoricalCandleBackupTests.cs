using System.Text.Json;
using System.IO.Compression;
using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.MarketData;

namespace TradingSystem.ContractTests;

public sealed class UpstoxHistoricalCandleBackupTests
{
    [Theory]
    [InlineData(20, "/v3/historical-candle/intraday/BSE_INDEX%7CSENSEX/minutes/1")]
    [InlineData(19, "/v3/historical-candle/BSE_INDEX%7CSENSEX/minutes/1/2026-11-19/2026-11-19")]
    public async Task ReaderRoutesTheActualRequestAndKeepsCredentialsOffCatalog(int day, string path)
    {
        using var handler = new CandleHandler(day, path);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["ReplayBackup:UpstoxAnalyticsToken"] = "synthetic-test-token" }).Build();
        using var backup = new UpstoxHistoricalCandleBackup(new Factory(handler), configuration, new Clock());
        var instrument = new Instrument(Guid.NewGuid(), "BSE", "SENSEX", InstrumentSegment.Cash, InstrumentType.Index, DateTimeOffset.UtcNow);
        var result = await backup.ReadAsync(instrument, "SENSEX", new(2026, 11, day), CancellationToken.None);
        Assert.Equal(105m, Assert.Single(result.Candles).Close);
        Assert.Equal(2, handler.Requests);
    }

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class CandleHandler(int day, string path) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            if (request.RequestUri!.Host == "assets.upstox.com")
            {
                Assert.Null(request.Headers.Authorization);
                using var bytes = new MemoryStream();
                using (var gzip = new GZipStream(bytes, CompressionMode.Compress, leaveOpen: true))
                    gzip.Write(Encoding.UTF8.GetBytes("""[{"instrument_type":"INDEX","segment":"BSE_INDEX","instrument_key":"BSE_INDEX|SENSEX"}]"""));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes.ToArray()) });
            }
            Assert.Equal("api.upstox.com", request.RequestUri.Host);
            Assert.Equal(path, request.RequestUri.AbsolutePath);
            Assert.Equal("synthetic-test-token", request.Headers.Authorization?.Parameter);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                $$$"""{"status":"success","data":{"candles":[["2026-11-{{{day}}}T09:15:00+05:30",100,110,90,105,200,10]]}}""") });
        }
    }

    [Theory]
    [InlineData("NSE_INDEX|Nifty 50")]
    [InlineData("NSE_INDEX|Nifty Bank")]
    [InlineData("BSE_INDEX|SENSEX")]
    [InlineData("NSE_FO|61471")]
    [InlineData("BSE_FO|1100929")]
    public void SameDayRepairUsesIntradayIncludingAfterMarketClose(string key)
    {
        var afterClose = new DateTimeOffset(2026, 11, 20, 16, 20, 0, TimeSpan.FromHours(5.5));
        Assert.Equal($"https://api.upstox.com/v3/historical-candle/intraday/{Uri.EscapeDataString(key)}/minutes/1",
            UpstoxHistoricalCandleBackup.CandleUrl(key, new(2026, 11, 20), afterClose));
    }

    [Fact]
    public void EndpointSwitchesAtIndiaMidnightNotUtcMidnight()
    {
        var midnight = new DateTimeOffset(2026, 11, 20, 18, 30, 0, TimeSpan.Zero);
        Assert.Contains("/intraday/", UpstoxHistoricalCandleBackup.CandleUrl("BSE_FO|1", new(2026, 11, 20), midnight.AddTicks(-1)));
        Assert.Equal("https://api.upstox.com/v3/historical-candle/BSE_FO%7C1/minutes/1/2026-11-20/2026-11-20",
            UpstoxHistoricalCandleBackup.CandleUrl("BSE_FO|1", new(2026, 11, 20), midnight));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            UpstoxHistoricalCandleBackup.CandleUrl("BSE_FO|1", new(2026, 11, 22), midnight));
    }

    [Theory]
    [InlineData("NIFTY", "NSE", "Nifty 50")]
    [InlineData("BANKNIFTY", "NSE", "Nifty Bank")]
    [InlineData("SENSEX", "BSE", "SENSEX")]
    public void IndexMappingRequiresExactExchangeAndIdentity(string market, string exchange, string name)
    {
        var instrument = new Instrument(Guid.NewGuid(), exchange, market, InstrumentSegment.Cash, InstrumentType.Index, DateTimeOffset.UtcNow);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new { instrument_type = "INDEX", segment = exchange + "_INDEX", instrument_key = exchange + "_INDEX|" + name }));
        Assert.True(UpstoxHistoricalCandleBackup.Matches(json.RootElement, instrument, market));
        Assert.False(UpstoxHistoricalCandleBackup.Matches(json.RootElement, instrument, "WRONG"));
    }

    [Fact]
    public void FuturesMappingRejectsWrongExpiryAndLot()
    {
        var instrument = new Instrument(Guid.NewGuid(), "NSE", "NIFTYFUT", InstrumentSegment.FuturesAndOptions, InstrumentType.Future, DateTimeOffset.UtcNow);
        instrument.UpdateBrokerMetadata("123", "NSE-NIFTY-20261124-FUT", new(2026, 11, 24), null, 65, .05m);
        var expiry = new DateTimeOffset(2026, 11, 24, 0, 0, 0, TimeSpan.FromHours(5.5)).ToUnixTimeMilliseconds();
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new { instrument_type = "FUT", segment = "NSE_FO", instrument_key = "NSE_FO|123", underlying_symbol = "NIFTY", lot_size = 65, expiry }));
        Assert.True(UpstoxHistoricalCandleBackup.Matches(json.RootElement, instrument, "NIFTY"));
        instrument.UpdateBrokerMetadata("123", "NSE-NIFTY-20261124-FUT", new(2026, 11, 25), null, 65, .05m);
        Assert.False(UpstoxHistoricalCandleBackup.Matches(json.RootElement, instrument, "NIFTY"));
    }

    [Fact]
    public void ParsesOnlyCompletedSessionMinutesAndRejectsMalformedPrices()
    {
        using var valid = JsonDocument.Parse("""{"status":"success","data":{"candles":[["2026-11-16T09:15:00+05:30",100,110,90,105,200,10],["2026-11-16T15:30:00+05:30",100,110,90,105,200,10]]}}""");
        var result = UpstoxHistoricalCandleBackup.Parse(valid.RootElement, new(2026, 11, 16));
        Assert.Equal(new DateTimeOffset(2026, 11, 16, 3, 45, 0, TimeSpan.Zero),
            GrowwHistoricalCandleImporter.ParseIndiaTimestamp(Assert.Single(result.Candles).SourceTimestamp));
        using var invalid = JsonDocument.Parse("""{"status":"success","data":{"candles":[["2026-11-16T09:15:00+05:30",100,90,80,105,200,10]]}}""");
        Assert.Throws<InvalidDataException>(() => UpstoxHistoricalCandleBackup.Parse(invalid.RootElement, new(2026, 11, 16)));
    }
}
