using System.Net;
using System.Text;
using System.Globalization;
using Microsoft.Extensions.Options;
using TradingSystem.Application.Broker;
using TradingSystem.Infrastructure.Broker.Groww;

namespace TradingSystem.ContractTests;

public sealed class GrowwReadOnlyContractTests
{
    [Fact]
    public async Task ProfileMapsOfficialResponseAndRequiredHeaders()
    {
        HttpRequestMessage? captured = null;
        var gateway = CreateGateway(request =>
        {
            captured = request;
            return Json(HttpStatusCode.OK, """
                {"status":"SUCCESS","payload":{"vendor_user_id":"vendor-1","ucc":"924189","nse_enabled":true,"bse_enabled":false,"ddpi_enabled":false,"active_segments":["CASH","FNO"]}}
                """);
        });

        var profile = await gateway.GetUserProfileAsync(CancellationToken.None);

        Assert.Equal("vendor-1", profile.VendorUserId);
        Assert.Equal(["CASH", "FNO"], profile.ActiveSegments);
        Assert.Equal("Bearer", captured!.Headers.Authorization!.Scheme);
        Assert.Equal("test-token", captured.Headers.Authorization.Parameter);
        Assert.Equal("1.0", Assert.Single(captured.Headers.GetValues("X-API-VERSION")));
        Assert.Equal("/v1/user/detail", captured.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task QuoteUsesDocumentedQueryAndMapsOpenInterest()
    {
        HttpRequestMessage? captured = null;
        var gateway = CreateGateway(request =>
        {
            captured = request;
            return Json(HttpStatusCode.OK, """
                {"status":"SUCCESS","payload":{"last_price":24500.25,"last_trade_time":1746174479582,"bid_price":24500.2,"bid_quantity":50,"offer_price":24500.3,"offer_quantity":25,"volume":1000,"open_interest":2000,"oi_day_change":100}}
                """);
        });

        var quote = await gateway.GetQuoteAsync(
            new GrowwQuoteRequest("NSE", "FNO", "NIFTY25AUGFUT"),
            CancellationToken.None);

        Assert.Equal(24500.25m, quote.LastPrice);
        Assert.Equal(2000m, quote.OpenInterest);
        Assert.Contains("exchange=NSE", captured!.RequestUri!.Query, StringComparison.Ordinal);
        Assert.Contains("segment=FNO", captured.RequestUri.Query, StringComparison.Ordinal);
        Assert.Contains("trading_symbol=NIFTY25AUGFUT", captured.RequestUri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HistoricalCandlesMapSevenFieldOfficialShape()
    {
        var gateway = CreateGateway(_ => Json(HttpStatusCode.OK, """
            {"status":"SUCCESS","payload":{"candles":[["2025-09-24T10:30:00",245.95,246.15,245.05,245.6,735060,1200]],"closing_price":245.6,"interval_in_minutes":5}}
            """));

        var result = await gateway.GetHistoricalCandlesAsync(
            new GrowwHistoricalCandleRequest(
                "NSE", "FNO", "NSE-NIFTY-25Sep25-FUT",
                DateTimeOffset.Parse("2025-09-24T05:00:00Z", CultureInfo.InvariantCulture),
                DateTimeOffset.Parse("2025-09-24T06:00:00Z", CultureInfo.InvariantCulture),
                "5minute"),
            CancellationToken.None);

        var candle = Assert.Single(result.Candles);
        Assert.Equal(735060, candle.Volume);
        Assert.Equal(1200m, candle.OpenInterest);
        Assert.Equal(5, result.IntervalInMinutes);
    }

    [Fact]
    public async Task ApiFailurePreservesDocumentedCodeWithoutLeakingToken()
    {
        var gateway = CreateGateway(_ => Json(HttpStatusCode.Unauthorized, """
            {"status":"FAILURE","error":{"code":"GA005","message":"Not authorised","metadata":null}}
            """));

        var exception = await Assert.ThrowsAsync<GrowwApiException>(() =>
            gateway.GetUserProfileAsync(CancellationToken.None));

        Assert.Equal("GA005", exception.ErrorCode);
        Assert.Equal(401, exception.StatusCode);
        Assert.DoesNotContain("test-token", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RateLimitAndMalformedPayloadFailClosed()
    {
        var throttled = CreateGateway(_ => Json(HttpStatusCode.TooManyRequests, "{}"));
        var malformed = CreateGateway(_ => Json(HttpStatusCode.OK, "{not-json"));

        var throttleException = await Assert.ThrowsAsync<GrowwApiException>(() =>
            throttled.GetUserProfileAsync(CancellationToken.None));
        var malformedException = await Assert.ThrowsAsync<GrowwApiException>(() =>
            malformed.GetUserProfileAsync(CancellationToken.None));

        Assert.Equal(429, throttleException.StatusCode);
        Assert.Equal("MALFORMED_RESPONSE", malformedException.ErrorCode);
    }

    [Fact]
    public async Task InstrumentCsvSupportsQuotedFieldsAndOfficialColumns()
    {
        const string csv = "exchange,exchange_token,trading_symbol,groww_symbol,name,instrument_type,segment,series,isin,underlying_symbol,underlying_exchange_token,expiry_date,strike_price,lot_size,tick_size,freeze_quantity,is_reserved,buy_allowed,sell_allowed\r\n" +
                           "NSE,26000,NIFTY,NSE-NIFTY,\"Nifty, 50\",IDX,CASH,,,,,,,1,0.05,,0,1,1\r\n";
        var gateway = CreateGateway(request =>
            request.RequestUri!.Host.Contains("assets", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(csv, Encoding.UTF8, "text/csv")
                }
                : throw new InvalidOperationException());

        var instrument = Assert.Single(
            await gateway.GetInstrumentMasterAsync(CancellationToken.None));

        Assert.Equal("NSE-NIFTY", instrument.GrowwSymbol);
        Assert.Equal(0.05m, instrument.TickSize);
        Assert.True(instrument.BuyAllowed);
    }

    [Fact]
    public async Task CancellationIsPropagatedBeforeNetworkCall()
    {
        var calls = 0;
        var gateway = CreateGateway(_ =>
        {
            calls++;
            return Json(HttpStatusCode.OK, "{}");
        });
        using var source = new CancellationTokenSource();
        source.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            gateway.GetUserProfileAsync(source.Token));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void ReadOnlyContractContainsNoOrderMutation()
    {
        var methods = typeof(IGrowwReadOnlyGateway).GetMethods();

        Assert.DoesNotContain(methods, method =>
            method.Name.Contains("Order", StringComparison.OrdinalIgnoreCase) ||
            method.Name.Contains("Trade", StringComparison.OrdinalIgnoreCase));
    }

    private static GrowwReadOnlyGateway CreateGateway(
        Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new StubHandler(responder);
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.groww.in/") };
        return new GrowwReadOnlyGateway(
            new StubHttpClientFactory(client),
            new StubTokenProvider(),
            Options.Create(new GrowwOptions()));
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(responder(request));
        }
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubTokenProvider : IGrowwAccessTokenProvider
    {
        public ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult("test-token");
        }
    }
}
