using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TradingSystem.Application.MarketData;
using TradingSystem.Infrastructure.MarketData;

namespace TradingSystem.ContractTests;

public sealed class UpstoxChartDisplayTests
{
    private static readonly string[] Codes = ["nifty", "sensex", "bank-nifty"];
    private static readonly DateTimeOffset At = new(2026, 11, 16, 11, 50, 10, TimeSpan.FromHours(5.5));
    private sealed class Clock : TimeProvider { public DateTimeOffset Now = At; public override DateTimeOffset GetUtcNow() => Now; }
    private static WorkspaceCandle Bar(DateTimeOffset at) => new(at, 60, 100, 102, 99, 101, 0, true);
    private static TradingWorkspaceSnapshot View(string code) => new(code, code == "sensex" ? "BSE" : "NSE", "1m", "Paper",
        "Disconnected", false, false, null, At, "Groww blocked", [], [], [],
        new("DataUnavailable", false, "Groww blocked", At, 0, 0, 0, null, null, null, null, null, null),
        [new(At.AddMinutes(-1), 1000, true)], MarketCode: code);

    [Theory]
    [InlineData("NIFTY", "nifty")]
    [InlineData("SENSEX", "sensex")]
    [InlineData("BANKNIFTY", "bank-nifty")]
    public void ChartFallbackCannotChangeTradingReadinessOrPositions(string market, string code)
    {
        var clock = new Clock(); var cache = new WorkspaceChartCache(clock); var original = View(code);
        cache.Store(code, new(2026, 11, 16), [Bar(At.AddSeconds(-10))]);
        cache.Observe(market, 105, At);
        var result = cache.Apply(original);
        Assert.Equal("Upstox", result.ChartDisplay!.Source);
        Assert.True(result.ChartDisplay.IsFresh);
        Assert.Equal(105, result.ChartDisplay.LastPrice);
        Assert.False(result.IsFresh); Assert.False(result.IsLive);
        Assert.Equal(original.FeedStatus, result.FeedStatus);
        Assert.Equal(original.LastMarketTimestampUtc, result.LastMarketTimestampUtc);
        Assert.Same(original.PaperAutomation, result.PaperAutomation);
        Assert.Same(original.Overlays, result.Overlays);
        Assert.Same(original.Evaluations, result.Evaluations);
        Assert.Empty(result.FuturesVolume!);
        Assert.NotEmpty(result.Candles);
        Assert.Empty(original.Candles);
        Assert.NotEmpty(original.FuturesVolume!);
        clock.Now = At.AddSeconds(31);
        Assert.False(cache.Apply(original).ChartDisplay!.IsFresh);
        clock.Now = At.AddDays(1);
        Assert.Same(original, cache.Apply(original));
    }

    [Fact]
    public void CompletedMinuteUsesCloseTimeAndKeepsQuoteAndCandleFreshnessSeparate()
    {
        foreach (var (market, code) in new[] { ("NIFTY", "nifty"), ("SENSEX", "sensex"), ("BANKNIFTY", "bank-nifty") })
        {
            // Reproduce production: 12:40 bar returned at 12:41:32 (92 seconds
            // from open, but only 32 seconds since the minute completed).
            var clock = new Clock { Now = At.AddSeconds(22) };
            var cache = new WorkspaceChartCache(clock);
            var original = View(code);
            cache.Store(code, new(2026, 11, 16), [Bar(At.AddSeconds(-70))]);
            cache.Observe(market, 105, clock.Now);
            var result = cache.Apply(original);
            Assert.True(result.ChartDisplay!.IsFresh);
            Assert.True(result.ChartDisplay.QuoteIsFresh);
            Assert.True(result.ChartDisplay.CandlesAreFresh);
            Assert.Equal(At.AddSeconds(-10), result.ChartDisplay.LatestCandleEndUtc);
            Assert.Equal(clock.Now, result.ChartDisplay.EvaluatedAtUtc);
            Assert.Equal(original.ObservedAtUtc, result.ObservedAtUtc);
            Assert.Same(original.PaperAutomation, result.PaperAutomation);
            Assert.False(result.IsFresh);

            clock.Now = At.AddSeconds(52); // Quote exactly 30s old remains valid.
            Assert.True(cache.Apply(original).ChartDisplay!.IsFresh);
            clock.Now = clock.Now.AddSeconds(1);
            Assert.False(cache.Apply(original).ChartDisplay!.QuoteIsFresh);
            Assert.False(cache.Apply(original).ChartDisplay!.IsFresh);

            clock.Now = At.AddSeconds(81); // Frozen candle >90s past its close.
            cache.Observe(market, 106, clock.Now);
            var delayed = cache.Apply(original).ChartDisplay!;
            Assert.True(delayed.QuoteIsFresh);
            Assert.False(delayed.CandlesAreFresh);
            Assert.False(delayed.IsFresh);
            cache.Store(code, new(2026, 11, 16), [Bar(At.AddSeconds(50)) with { IsClosed = false }]);
            Assert.True(cache.Apply(original).ChartDisplay!.IsFresh);
        }
    }

    [Fact]
    public void InvalidQuotesAndPrimaryRecoveryCannotMislabelTheChart()
    {
        var clock = new Clock(); var cache = new WorkspaceChartCache(clock); var original = View("nifty");
        cache.Store("nifty", new(2026, 11, 16), [Bar(At.AddSeconds(-10))]);
        cache.Observe("NIFTY", 1, At.AddSeconds(1));
        cache.Observe("NIFTY", 1, At.AddSeconds(-31));
        cache.Observe("NIFTY", 0, At);
        Assert.False(cache.Apply(original).ChartDisplay!.IsFresh);
        cache.Observe("NIFTY", 105, At);
        cache.Observe("NIFTY", 99, At.AddSeconds(-1));
        Assert.Equal(105, cache.Apply(original).ChartDisplay!.LastPrice);
        var recovered = original with { IsFresh = true, IsLive = true, Candles = [Bar(At.AddSeconds(-10))] };
        Assert.Same(recovered, cache.Apply(recovered));
        // A new quote with yesterday's/absent candles does not hide the fallback.
        Assert.NotNull(cache.Apply(original with { IsFresh = true }).ChartDisplay);
        Assert.Null(cache.Apply(View("natural-gas")).ChartDisplay);
    }

    [Fact]
    public async Task AllThreeUseOnlyIntradayReadEndpointsAndSharedDisplayCache()
    {
        var clock = new Clock(); var cache = new WorkspaceChartCache(clock); var handler = new Api();
        using var service = new UpstoxChartDisplay(new Factory(handler), new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["ReplayBackup:Host"] = "Website", ["ReplayBackup:UpstoxAnalyticsToken"] = "read-only-test" }).Build(),
            cache, new(), clock, NullLogger<UpstoxChartDisplay>.Instance);
        foreach (var market in new[] { "NIFTY", "SENSEX", "BANKNIFTY" }) cache.Observe(market, 105, At);
        await service.StartAsync(CancellationToken.None);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (Codes.Any(code => cache.Apply(View(code)).ChartDisplay is null))
                await Task.Delay(10, timeout.Token);
            Assert.Equal(3, handler.Requests.Count);
            Assert.Equal(3, handler.Requests.Distinct().Count());
            foreach (var code in Codes)
            {
                Assert.True(cache.Apply(View(code)).ChartDisplay!.IsFresh);
                Assert.Equal(At.AddSeconds(-10), cache.Apply(View(code)).Candles.Single().OpenTimeUtc);
            }
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    { public HttpClient CreateClient(string name) => new(handler, disposeHandler: false); }
    private sealed class Api : HttpMessageHandler
    {
        public ConcurrentBag<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("api.upstox.com", request.RequestUri!.Host);
            Assert.Contains("/v3/historical-candle/intraday/", request.RequestUri.AbsolutePath);
            Assert.EndsWith("/minutes/1", request.RequestUri.AbsolutePath);
            Assert.Equal("read-only-test", request.Headers.Authorization!.Parameter);
            Requests.Add(request.RequestUri.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                "{\"status\":\"success\",\"data\":{\"candles\":[[\"2026-11-16T11:50:00+05:30\",100,102,99,101,0,0]]}}") });
        }
    }
}
