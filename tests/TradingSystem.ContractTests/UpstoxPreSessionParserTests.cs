using System.Text;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TradingSystem.Infrastructure.MarketData;

namespace TradingSystem.ContractTests;

public sealed class UpstoxPreSessionParserTests
{
    private static readonly DateTimeOffset Captured =
        new(2026, 10, 29, 3, 15, 0, TimeSpan.Zero);

    [Fact]
    public void GlobalMasterUsesDocumentedKeysAndLatency()
    {
        var json = """
            [
              {"name":"GIFT NIFTY","instrument_key":"GLOBAL_INDEX|SGX NIFTY","latency":"120 Seconds"},
              {"name":"Oil (Brent)","instrument_key":"GLOBAL_INDICATOR|BZUSD","latency":"20 Seconds"},
              {"name":"IGNORED","instrument_key":"NSE_EQ|123","latency":"20 Seconds"}
            ]
            """;
        var instruments = UpstoxPreSessionParser.ParseInstruments(Encoding.UTF8.GetBytes(json),
            ["GIFT NIFTY", "OIL (BRENT)"]);

        Assert.Equal(2, instruments.Count);
        Assert.Equal(120, instruments["GIFT NIFTY"].LatencySeconds);
        Assert.Equal("GLOBAL_INDICATOR|BZUSD", instruments["Oil (Brent)"].Key);
    }

    [Fact]
    public void QuotesIncludeOnlyTimestampedFreshInstruments()
    {
        var instruments = new Dictionary<string, UpstoxInstrument>
        {
            ["GIFT NIFTY"] = new("GLOBAL_INDEX|SGX NIFTY", "GIFT NIFTY", 120),
            ["Oil (Brent)"] = new("GLOBAL_INDICATOR|BZUSD", "Oil (Brent)", 20),
            ["DOW JONES"] = new("GLOBAL_INDEX|^DJI", "DOW JONES", 900)
        };
        var recent = Captured.AddMinutes(-3).ToUnixTimeMilliseconds();
        var stale = Captured.AddHours(-3).ToUnixTimeMilliseconds();
        var overnight = Captured.AddHours(-8).ToUnixTimeMilliseconds();
        var json = """
            {"status":"success","data":{
              "a":{"instrument_token":"GLOBAL_INDEX|SGX NIFTY","last_price":24000,"last_trade_time":"RECENT","net_change":100},
              "b":{"instrument_token":"GLOBAL_INDICATOR|BZUSD","last_price":70,"last_trade_time":"STALE"},
              "c":{"instrument_token":"GLOBAL_INDEX|^DJI","last_price":50000,"last_trade_time":"OVERNIGHT"}
            }}
            """.Replace("RECENT", recent.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("STALE", stale.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("OVERNIGHT", overnight.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

        var evidence = UpstoxPreSessionParser.ParseQuotes(json, instruments, Captured);

        Assert.Contains(evidence, item => item.Name == "GIFT Nifty" &&
            item.Value!.Contains("24000") && item.SourceAtUtc is not null);
        Assert.Contains(evidence, item => item.Name == "US and Asian markets" &&
            item.Value!.Contains("DOW JONES"));
        Assert.DoesNotContain(evidence, item => item.Name == "Crude oil");
    }

    [Fact]
    public void PriorSessionFlowsExcludeSameDayAndOldData()
    {
        var previous = new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero)
            .ToUnixTimeMilliseconds();
        var current = Captured.ToUnixTimeMilliseconds();
        var fii = """
            {"status":"success","data":{"NSE_EQ|CASH":[
              {"time_stamp":CURRENT,"buy_amount":1000,"sell_amount":0},
              {"time_stamp":PREVIOUS,"buy_amount":500,"sell_amount":600}
            ]}}
            """.Replace("CURRENT", current.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("PREVIOUS", previous.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        var dii = """
            {"status":"success","data":{"NSE_EQ|CASH":[
              {"time_stamp":PREVIOUS,"buy_amount":900,"sell_amount":700}
            ]}}
            """.Replace("PREVIOUS", previous.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

        var evidence = UpstoxPreSessionParser.ParseFlows(fii, dii, new DateOnly(2026, 10, 29));

        Assert.NotNull(evidence);
        Assert.Contains("FII 2026-10-28 net ₹-100", evidence.Value);
        Assert.Contains("DII 2026-10-28 net ₹+200", evidence.Value);
        Assert.DoesNotContain("1000", evidence.Value);
    }

    [Fact]
    public async Task NoConfiguredTokenMakesNoOutboundRequests()
    {
        var handler = new FeedHandler();
        var source = new UpstoxPreSessionSource(new HttpClient(handler),
            new ConfigurationBuilder().Build(), NullLogger<UpstoxPreSessionSource>.Instance);

        var result = await source.ReadAsync(new DateOnly(2026, 10, 29), Captured,
            CancellationToken.None);

        Assert.Empty(result);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task GlobalAndDiiEvidenceSurviveFailedFiiFeed()
    {
        var handler = new FeedHandler();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Upstox:AnalyticsToken"] = "test-token" }).Build();
        var source = new UpstoxPreSessionSource(new HttpClient(handler), configuration,
            NullLogger<UpstoxPreSessionSource>.Instance);

        var result = await source.ReadAsync(new DateOnly(2026, 10, 29), Captured,
            CancellationToken.None);

        Assert.Contains(result, item => item.Name == "GIFT Nifty");
        Assert.Contains(result, item => item.Name == "FII/DII prior-session flows" &&
            item.Value!.Contains("DII") && !item.Value.Contains("FII"));
        Assert.Equal(4, handler.CallCount);
    }

    private sealed class FeedHandler : HttpMessageHandler
    {
        private int _callCount;
        public int CallCount => _callCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            var uri = request.RequestUri!.AbsoluteUri;
            if (uri.Contains("global.json.gz", StringComparison.Ordinal))
            {
                const string master = """
                    [{"name":"GIFT NIFTY","instrument_key":"GLOBAL_INDEX|SGX NIFTY","latency":"120 Seconds"}]
                    """;
                using var output = new MemoryStream();
                using (var gzip = new GZipStream(output, CompressionMode.Compress, leaveOpen: true))
                using (var writer = new StreamWriter(gzip, Encoding.UTF8, leaveOpen: true))
                    writer.Write(master);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(output.ToArray())
                });
            }
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-token", request.Headers.Authorization?.Parameter);
            if (uri.Contains("/market/fii", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            if (uri.Contains("/market/dii", StringComparison.Ordinal))
            {
                var previous = new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero)
                    .ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                        {"status":"success","data":{"NSE_EQ|CASH":[
                          {"time_stamp":PREVIOUS,"buy_amount":900,"sell_amount":700}
                        ]}}
                        """.Replace("PREVIOUS", previous, StringComparison.Ordinal))
                });
            }
            var recent = Captured.AddMinutes(-3).ToUnixTimeMilliseconds()
                .ToString(CultureInfo.InvariantCulture);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"status":"success","data":{"gift":{"instrument_token":"GLOBAL_INDEX|SGX NIFTY",
                    "last_price":24000,"last_trade_time":"RECENT"}}}
                    """.Replace("RECENT", recent, StringComparison.Ordinal))
            });
        }
    }
}
